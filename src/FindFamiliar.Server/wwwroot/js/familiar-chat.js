// The window onto a server-owned conversation.
//
// This file holds no conversation state that matters. It holds a cursor — the highest turn sequence
// it has rendered — and everything else is re-read from the server. That is the whole design: a
// reload, a reconnect, a second device and a tab that slept for an hour all take the same path,
// which is "give me everything after N".
//
// The page works with this script disabled. The server renders the transcript on every request and
// the composer is an ordinary form post, so what follows is an enhancement to a page that already
// functions rather than the thing that makes it function.

(function () {
    "use strict";

    // The server closes a stream once nothing is in flight, so a reconnection is the ordinary end of
    // every connection rather than a sign of trouble. Two cadences: prompt while a reply is arriving,
    // relaxed when the conversation is at rest — the slow one still notices a reply somebody started
    // on another device, without polling hard for something that is not happening.
    const ACTIVE_RECONNECT_MS = 1000;
    const IDLE_RECONNECT_MS = 5000;
    const MAX_RECONNECT_MS = 30000;

    const root = document.querySelector("[data-familiar-chat]");

    if (!root || !window.EventSource) {
        // No EventSource means an old browser, and the server-rendered page plus its meta refresh is
        // already a correct, if less pleasant, experience. Leaving it alone is the right failure.
        return;
    }

    const chatId = root.dataset.chatId;
    const transcript = root.querySelector("[data-transcript]");
    const status = root.querySelector("[data-stream-status]");

    // The only durable client state. Everything rendered can be derived from the server given this.
    let cursor = Number(root.dataset.cursor || "0");
    let source = null;
    let reconnectDelay = ACTIVE_RECONNECT_MS;
    let idle = false;
    let closed = false;

    // A page rendered while a reply was arriving carries a meta refresh as its no-script fallback.
    // Once this script is running, that refresh would throw away a stream mid-sentence, so it goes.
    const metaRefresh = document.querySelector('meta[http-equiv="refresh"]');
    if (metaRefresh) {
        metaRefresh.remove();
    }

    function setStatus(text) {
        if (!status) {
            return;
        }

        status.textContent = text || "";
        status.hidden = !text;
    }

    // ---------------------------------------------------------------- rendering

    // textContent everywhere, never innerHTML. Turn output is model-written text, and the rule that
    // it is inert has to hold on this path exactly as it does in the Razor page.
    function renderTurn(turn) {
        const existing = transcript.querySelector('[data-turn-sequence="' + turn.sequence + '"]');
        const node = existing || buildTurn(turn.sequence);

        node.querySelector("[data-user-text]").textContent = turn.userText;

        const outputNode = node.querySelector("[data-output]");
        const pendingNode = node.querySelector("[data-pending]");
        const inFlight = turn.state === "Pending" || turn.state === "Generating";

        renderOutput(outputNode, turn);
        outputNode.hidden = !turn.output;

        // The waiting note shows only while nothing has arrived yet. Once text is streaming, the text
        // itself is the evidence that something is happening.
        pendingNode.hidden = !(inFlight && !turn.output);

        const reply = node.querySelector("[data-reply]");
        reply.className = "familiar-message " + stateCss(turn.state);
        node.querySelector("[data-reply-author]").textContent = stateLabel(turn.state);

        const failed = node.querySelector("[data-failed]");
        failed.hidden = turn.state !== "Failed";

        if (!existing) {
            transcript.appendChild(node);
        }
    }

    // The canonical 8-4-4-4-12 form, bounded so the tail of a longer token cannot parse as an id and
    // invent a citation out of something that was never one. Mirrors FamiliarChatCitations.Segment;
    // the two must agree, because a page that arrived by render and one built here have to be
    // indistinguishable.
    const ID_PATTERN =
        /(^|[^0-9A-Za-z-])([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})(?![0-9A-Za-z-])/g;

    // Text is always textContent; only the chip is an element. Turn output is model-written and the
    // rule that it is inert holds here exactly as it does in the Razor page.
    function renderOutput(node, turn) {
        node.textContent = "";

        const output = turn.output || "";
        const cited = {};

        (turn.citations || []).forEach(function (citation) {
            cited[String(citation.entryId).toLowerCase()] = citation;
        });

        let last = 0;
        let match;

        ID_PATTERN.lastIndex = 0;

        while ((match = ID_PATTERN.exec(output)) !== null) {
            const start = match.index + match[1].length;

            if (start > last) {
                node.appendChild(document.createTextNode(output.slice(last, start)));
            }

            node.appendChild(chip(cited[match[2].toLowerCase()]));
            last = start + match[2].length;
        }

        if (last < output.length) {
            node.appendChild(document.createTextNode(output.slice(last)));
        }
    }

    function chip(citation) {
        if (!citation) {
            // Named, never silently deleted: a reply citing something it was never shown is the most
            // diagnostic thing it can do, and a reader is entitled to see it happen.
            const unsupported = document.createElement("span");
            unsupported.className = "familiar-citation is-unsupported";
            unsupported.title = "This reference was not among the entries this answer was given.";
            unsupported.textContent = "unsupported reference";
            return unsupported;
        }

        const link = document.createElement("a");
        link.className = "familiar-citation";
        // The route form the Razor page produces, not a query string. The two renderers must build
        // the same link or a chip tapped on a streamed reply lands somewhere a rendered one does not.
        link.href = "/Demiplane/" + encodeURIComponent(citation.projectId);
        link.title = citation.kind + " — " + citation.title;
        link.textContent = String(citation.kind).toLowerCase() + ": " + citation.title;
        return link;
    }

    function stateCss(state) {
        if (state === "Completed") {
            return "is-familiar";
        }

        return state === "Failed" ? "is-system" : "is-pending";
    }

    function stateLabel(state) {
        if (state === "Completed") {
            return "Familiar";
        }

        if (state === "Failed") {
            return "Find Familiar";
        }

        return state === "Generating" ? "Replying" : "Queued";
    }

    // The same shape the server renders, so a page that arrived by render and one built here are
    // indistinguishable. Only the static scaffolding is set as markup; every value is textContent.
    function buildTurn(sequence) {
        const item = document.createElement("li");
        item.dataset.turnSequence = String(sequence);
        item.className = "familiar-turn";
        item.innerHTML =
            '<div class="familiar-message is-human">' +
            '<p class="familiar-message-meta"><span class="familiar-message-author">You</span></p>' +
            '<p class="conversation-block" data-user-text></p>' +
            "</div>" +
            '<div class="familiar-message is-pending" data-reply>' +
            '<p class="familiar-message-meta">' +
            '<span class="familiar-message-author" data-reply-author></span>' +
            '<span data-failed hidden> · <strong>Failed</strong></span>' +
            "</p>" +
            '<p class="conversation-block" data-output hidden></p>' +
            '<p role="status" data-pending hidden>Working on this. It is being generated on the ' +
            "server, so you can close this page — the reply will be here when you come back.</p>" +
            "</div>";

        return item;
    }

    // ---------------------------------------------------------------- the stream

    function applyPage(page) {
        (page.turns || []).forEach(renderTurn);

        // The server says where to resume from. The rule is subtle — the cursor stops *before* a turn
        // still arriving, so the next request does not skip a half-written reply — and computing it
        // here as well would be a second copy that could drift from the first.
        if (typeof page.resumeCursor === "number") {
            cursor = page.resumeCursor;
        }

        return page.hasTurnInFlight;
    }

    function connect() {
        if (closed || source) {
            return;
        }

        source = new EventSource("/api/familiar/chats/" + chatId + "/stream?after=" + cursor);

        source.addEventListener("turns", function (event) {
            let page;

            try {
                page = JSON.parse(event.data);
            } catch (error) {
                // An unreadable frame is not a reason to tear the page down. The next one, or the
                // next reconnection, resyncs from the cursor.
                return;
            }

            // A successful frame means the connection works, so the backoff resets. Otherwise one bad
            // patch of network would leave every later reconnection slow.
            const inFlight = applyPage(page);
            idle = !inFlight;
            reconnectDelay = idle ? IDLE_RECONNECT_MS : ACTIVE_RECONNECT_MS;
            setStatus("");
        });

        source.addEventListener("error", function () {
            // EventSource reconnects on its own, but it resumes the same URL with the same cursor,
            // which would replay from where this connection started rather than from where the page
            // actually is. Closing and reconnecting ourselves is what makes the resume honest.
            disconnect();

            if (closed) {
                return;
            }

            // Only says so when a reply was actually arriving. An idle reconnection is routine and
            // announcing it would train the reader to ignore the one that matters.
            if (!idle) {
                setStatus("Reconnecting…");
            }

            const delay = reconnectDelay;
            reconnectDelay = Math.min(reconnectDelay * 2, MAX_RECONNECT_MS);

            window.setTimeout(connect, delay);
        });
    }

    function disconnect() {
        if (source) {
            source.close();
            source = null;
        }
    }

    // ---------------------------------------------------------------- mobile

    // iOS Safari suspends a backgrounded tab, and a suspended EventSource wakes up believing it is
    // current when it is not. An out-of-date device that looks current is worse than one that
    // obviously needs a refresh, so waking always re-reads the gap before trusting any stream.
    document.addEventListener("visibilitychange", function () {
        if (document.visibilityState === "hidden") {
            disconnect();
            return;
        }

        catchUp();
    });

    // The same recovery for a wifi/cellular handoff, which drops the connection without the tab ever
    // being hidden.
    window.addEventListener("online", catchUp);
    window.addEventListener("pageshow", catchUp);

    function catchUp() {
        if (closed) {
            return;
        }

        disconnect();
        setStatus("Catching up…");

        fetch("/api/familiar/chats/" + chatId + "/turns?after=" + cursor, {
            headers: { Accept: "application/json" },
            cache: "no-store"
        })
            .then(function (response) {
                if (!response.ok) {
                    throw new Error("gap fetch failed");
                }

                return response.json();
            })
            .then(function (page) {
                applyPage(page);
                setStatus("");
                connect();
            })
            .catch(function () {
                // Say so rather than showing a stale transcript as though it were current.
                setStatus("Not connected. Reload to see the latest.");
                window.setTimeout(catchUp, 5000);
            });
    }

    window.addEventListener("pagehide", function () {
        closed = true;
        disconnect();
    });

    connect();
})();
