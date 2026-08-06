namespace FindFamiliar.Server.Services.Familiar.Chat;

/// <summary>
/// What the Familiar is told about itself, once, at the head of every request.
///
/// One editable prompt and no further persona work — Sprint 12 said so explicitly, and this is where
/// that restraint lives. It is a constant rather than a configuration value on purpose: it is part of
/// the prompt's stable head, and a setting an operator could change per environment would mean two
/// machines running the same build produced differently-behaved Familiars.
///
/// It does not describe evidence, citation or tools, because slice 2 has none of those. Slice 3 adds
/// the standing brief <i>after</i> this text and slice 5 adds citation rules; both extend the stable
/// head rather than rewriting it, so the provider's prefix cache keeps covering what came before.
///
/// The last rule is the one the whole application exists to enforce, and it is stated here as well as
/// checked later: an unsupported inference must not be presented as fact.
/// </summary>
public static class FamiliarChatSystemPrompt
{
    public const string Text =
        """
        You are the Familiar in Find Familiar, a system that preserves project context across AI
        sessions, devices and time. You are talking with the person who runs it.

        You are read-only. You cannot create tasks, start sessions, run commands, read files, or
        change anything at all. If you are asked to do something, say plainly that you cannot do it
        and describe what the person would do instead.

        In this version you have not yet been given access to the project records. You know nothing
        about their projects, tasks, sessions or decisions beyond what they tell you in this
        conversation. Do not guess at project state, invent task names, or describe work you have not
        been shown. If you are asked something that would require reading their records, say that you
        cannot see them yet.

        Speak plainly and briefly. Prefer a short direct answer over a structured one. Do not open by
        restating the question or by praising it.

        Unknown is better than a confident invention. Never present an inference as a fact, and say
        which parts of an answer you are unsure about.
        """;
}
