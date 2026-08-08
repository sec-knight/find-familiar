using FindFamiliar.Server.Domain;
using Microsoft.EntityFrameworkCore;

namespace FindFamiliar.Server.Data;

public sealed class FamiliarDbContext(DbContextOptions<FamiliarDbContext> options) : DbContext(options)
{
    public DbSet<FamiliarProject> Projects => Set<FamiliarProject>();

    public DbSet<FamiliarTask> Tasks => Set<FamiliarTask>();

    public DbSet<AgentSession> AgentSessions => Set<AgentSession>();

    public DbSet<ContextEntry> ContextEntries => Set<ContextEntry>();

    public DbSet<Worker> Workers => Set<Worker>();

    public DbSet<Conversation> Conversations => Set<Conversation>();

    public DbSet<ConversationMessage> ConversationMessages => Set<ConversationMessage>();

    public DbSet<WorkProposal> WorkProposals => Set<WorkProposal>();

    public DbSet<SessionHandoff> SessionHandoffs => Set<SessionHandoff>();

    public DbSet<FamiliarConversation> FamiliarConversations => Set<FamiliarConversation>();

    public DbSet<FamiliarMessage> FamiliarMessages => Set<FamiliarMessage>();

    public DbSet<FamiliarEvidence> FamiliarEvidence => Set<FamiliarEvidence>();

    public DbSet<FamiliarActionProposal> FamiliarActionProposals => Set<FamiliarActionProposal>();

    public DbSet<FamiliarChat> FamiliarChats => Set<FamiliarChat>();

    public DbSet<FamiliarChatTurn> FamiliarChatTurns => Set<FamiliarChatTurn>();

    public DbSet<FamiliarPlanProposal> FamiliarPlanProposals => Set<FamiliarPlanProposal>();

    public DbSet<FamiliarPlanItem> FamiliarPlanItems => Set<FamiliarPlanItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FamiliarProject>(entity =>
        {
            entity.ToTable("Projects");
            entity.HasKey(project => project.Id);
            entity.Property(project => project.Name).HasMaxLength(160).IsRequired();
            entity.HasIndex(project => project.Name).IsUnique();
            entity.Property(project => project.Purpose).HasMaxLength(4_000).IsRequired();
            entity.Property(project => project.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(project => project.ContextRevision).HasDefaultValue(0);
            entity.HasMany(project => project.Tasks)
                .WithOne(task => task.Project)
                .HasForeignKey(task => task.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(project => project.ContextEntries)
                .WithOne(entry => entry.Project)
                .HasForeignKey(entry => entry.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FamiliarTask>(entity =>
        {
            entity.ToTable("Tasks");
            entity.HasKey(task => task.Id);
            entity.Property(task => task.Title).HasMaxLength(200).IsRequired();
            entity.Property(task => task.RequestedOutcome).HasMaxLength(4_000).IsRequired();
            entity.Property(task => task.Status).HasConversion<string>().HasMaxLength(32);
            entity.HasIndex(task => new { task.ProjectId, task.Status });
            entity.HasMany(task => task.AgentSessions)
                .WithOne(session => session.Task)
                .HasForeignKey(session => session.TaskId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(task => task.ContextEntries)
                .WithOne(entry => entry.Task)
                .HasForeignKey(entry => entry.TaskId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AgentSession>(entity =>
        {
            entity.ToTable("AgentSessions");
            entity.HasKey(session => session.Id);
            entity.Property(session => session.Role).HasConversion<string>().HasMaxLength(32);
            entity.Property(session => session.Provider).HasMaxLength(120);
            entity.Property(session => session.ExternalSessionReference).HasMaxLength(500);
            entity.Property(session => session.Status).HasConversion<string>().HasMaxLength(32).IsConcurrencyToken();
            entity.Property(session => session.ClaimId).IsConcurrencyToken();
            entity.HasIndex(session => new { session.TaskId, session.StartedUtc });
            // Supports the claim scan, which filters Started sessions by remaining lease.
            entity.HasIndex(session => new { session.Status, session.ClaimExpiresUtc });

            // At most one Started session per task, enforced by the database (ADR-0010).
            //
            // ADR-0005 deliberately deferred this: with a single human command path, an
            // authoritative read at command time was enough. Sprint 09 adds a second concurrent
            // session-creation writer — handoff approval — so the invariant now needs enforcement
            // that does not depend on a caller remembering to check. This index is authoritative
            // for the invariant across every write path, including direct SQL.
            //
            // The filter matches the stored TEXT because Status uses HasConversion<string>() above.
            // If that conversion is ever removed, this filter silently stops matching and the
            // invariant silently disappears — AgentSessionStartedUniqueIndexTests guards against it.
            entity.HasIndex(session => session.TaskId)
                .IsUnique()
                .HasFilter("\"Status\" = 'Started'")
                .HasDatabaseName("IX_AgentSessions_TaskId_Started");
            entity.HasMany(session => session.ContextEntries)
                .WithOne(entry => entry.SourceSession)
                .HasForeignKey(entry => entry.SourceSessionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(session => session.ClaimedByWorker)
                .WithMany(worker => worker.ClaimedSessions)
                .HasForeignKey(session => session.ClaimedByWorkerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Worker>(entity =>
        {
            entity.ToTable("Workers");
            entity.HasKey(worker => worker.Id);
            entity.Property(worker => worker.WorkerKey).HasMaxLength(100).IsRequired();
            entity.HasIndex(worker => worker.WorkerKey).IsUnique();
            entity.Property(worker => worker.DisplayName).HasMaxLength(160).IsRequired();
            entity.Property(worker => worker.Capabilities).HasMaxLength(WorkerCapabilities.MaxLength).IsRequired();
            entity.Property(worker => worker.Enabled).HasDefaultValue(true);
        });

        modelBuilder.Entity<Conversation>(entity =>
        {
            entity.ToTable("Conversations");
            entity.HasKey(conversation => conversation.Id);
            entity.Property(conversation => conversation.Status).HasConversion<string>().HasMaxLength(32);
            entity.HasIndex(conversation => new { conversation.Status, conversation.UpdatedUtc });
            entity.HasMany(conversation => conversation.Messages)
                .WithOne(message => message.Conversation)
                .HasForeignKey(message => message.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(conversation => conversation.Proposal)
                .WithOne(proposal => proposal.Conversation)
                .HasForeignKey<WorkProposal>(proposal => proposal.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);

            // Restrict, not Cascade: deleting a conversation must never take approved work with
            // it, and an approved task/session may not be deleted while a conversation records it.
            entity.HasOne(conversation => conversation.ApprovedTask)
                .WithMany()
                .HasForeignKey(conversation => conversation.ApprovedTaskId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(conversation => conversation.ApprovedSession)
                .WithMany()
                .HasForeignKey(conversation => conversation.ApprovedSessionId)
                .OnDelete(DeleteBehavior.Restrict);

            // One workflow object can satisfy at most one approval. Filtered so the many
            // still-pending conversations, which all hold NULL, do not collide.
            entity.HasIndex(conversation => conversation.ApprovedTaskId)
                .IsUnique()
                .HasFilter("\"ApprovedTaskId\" IS NOT NULL");
            entity.HasIndex(conversation => conversation.ApprovedSessionId)
                .IsUnique()
                .HasFilter("\"ApprovedSessionId\" IS NOT NULL");
        });

        modelBuilder.Entity<ConversationMessage>(entity =>
        {
            entity.ToTable("ConversationMessages");
            entity.HasKey(message => message.Id);
            entity.Property(message => message.Author).HasConversion<string>().HasMaxLength(32);
            entity.Property(message => message.Content)
                .HasMaxLength(ConversationMessage.MaxContentLength)
                .IsRequired();
            // Unique, so two racing appends can never produce an ambiguous display order.
            entity.HasIndex(message => new { message.ConversationId, message.Sequence }).IsUnique();
        });

        modelBuilder.Entity<WorkProposal>(entity =>
        {
            entity.ToTable("WorkProposals");
            entity.HasKey(proposal => proposal.Id);
            // One current proposal per conversation.
            entity.HasIndex(proposal => proposal.ConversationId).IsUnique();
            entity.Property(proposal => proposal.Title).HasMaxLength(WorkProposal.MaxTitleLength).IsRequired();
            entity.Property(proposal => proposal.RequestedOutcome)
                .HasMaxLength(WorkProposal.MaxRequestedOutcomeLength)
                .IsRequired();
            entity.Property(proposal => proposal.Role).HasConversion<string>().HasMaxLength(32);
            entity.Property(proposal => proposal.Status).HasConversion<string>().HasMaxLength(32);
            entity.HasIndex(proposal => new { proposal.Status, proposal.ConcurrencyToken });
            entity.HasOne(proposal => proposal.Project)
                .WithMany()
                .HasForeignKey(proposal => proposal.ProjectId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SessionHandoff>(entity =>
        {
            entity.ToTable("SessionHandoffs");
            entity.HasKey(handoff => handoff.Id);
            entity.Property(handoff => handoff.SourceOutcome).HasConversion<string>().HasMaxLength(32);
            entity.Property(handoff => handoff.ProposedRole).HasConversion<string>().HasMaxLength(32);
            entity.Property(handoff => handoff.Kind).HasConversion<string>().HasMaxLength(32);
            entity.Property(handoff => handoff.Status).HasConversion<string>().HasMaxLength(32);

            // One handoff per terminal session. A replayed result capture is already rejected by the
            // conditional Started transition; this is the database-level belt to that suspenders.
            entity.HasIndex(handoff => handoff.SourceSessionId).IsUnique();

            // At most one actionable handoff per task. This is what makes concurrent approval
            // trivially safe: contenders can only ever race for the same row, never for two rows
            // that would each start a session.
            entity.HasIndex(handoff => handoff.TaskId)
                .IsUnique()
                .HasFilter("\"Status\" = 'Pending'")
                .HasDatabaseName("IX_SessionHandoffs_TaskId_Pending");

            // One session was created by at most one handoff, mirroring Conversation.ApprovedSessionId.
            entity.HasIndex(handoff => handoff.CreatedSessionId)
                .IsUnique()
                .HasFilter("\"CreatedSessionId\" IS NOT NULL");

            entity.HasOne(handoff => handoff.Task)
                .WithMany()
                .HasForeignKey(handoff => handoff.TaskId)
                .OnDelete(DeleteBehavior.Cascade);

            // Cascade, not Restrict: deleting a task cascades to its sessions, and a Restrict here
            // would abort that cascade if SQLite removed the session rows first.
            entity.HasOne(handoff => handoff.SourceSession)
                .WithMany()
                .HasForeignKey(handoff => handoff.SourceSessionId)
                .OnDelete(DeleteBehavior.Cascade);

            // Restrict: a session a handoff claims to have created must never be deleted out from
            // under that claim.
            entity.HasOne(handoff => handoff.CreatedSession)
                .WithMany()
                .HasForeignKey(handoff => handoff.CreatedSessionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FamiliarConversation>(entity =>
        {
            entity.ToTable("FamiliarConversations");
            entity.HasKey(conversation => conversation.Id);

            // One conversation per project. The subject of the conversation is the project, and
            // continuity across days is the point; relaxing this index is all that multiple threads
            // per project would need later.
            entity.HasIndex(conversation => conversation.ProjectId).IsUnique();

            entity.HasOne(conversation => conversation.Project)
                .WithMany()
                .HasForeignKey(conversation => conversation.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(conversation => conversation.Messages)
                .WithOne(message => message.Conversation)
                .HasForeignKey(message => message.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(conversation => conversation.Proposals)
                .WithOne(proposal => proposal.Conversation)
                .HasForeignKey(proposal => proposal.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FamiliarMessage>(entity =>
        {
            entity.ToTable("FamiliarMessages");
            entity.HasKey(message => message.Id);
            entity.Property(message => message.Author).HasConversion<string>().HasMaxLength(32);
            entity.Property(message => message.Delivery).HasConversion<string>().HasMaxLength(32);
            entity.Property(message => message.Content)
                .HasMaxLength(FamiliarMessage.MaxContentLength)
                .IsRequired();
            entity.Property(message => message.ProviderName).HasMaxLength(FamiliarMessage.MaxProviderNameLength);
            entity.Property(message => message.ProviderModel).HasMaxLength(FamiliarMessage.MaxProviderModelLength);
            entity.Property(message => message.FailureCode).HasMaxLength(FamiliarMessage.MaxFailureCodeLength);

            // Unique, so two racing appends can never produce an ambiguous display order.
            entity.HasIndex(message => new { message.ConversationId, message.Sequence }).IsUnique();

            entity.HasMany(message => message.Evidence)
                .WithOne(evidence => evidence.Message)
                .HasForeignKey(evidence => evidence.MessageId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FamiliarEvidence>(entity =>
        {
            entity.ToTable("FamiliarEvidence");
            entity.HasKey(evidence => evidence.Id);
            entity.Property(evidence => evidence.Kind).HasConversion<string>().HasMaxLength(32);
            entity.Property(evidence => evidence.Label)
                // Qualified: the DbSet property above shadows the entity type's simple name here.
                .HasMaxLength(Domain.FamiliarEvidence.MaxLabelLength)
                .IsRequired();

            // ReferenceId carries no foreign key on purpose. One nullable FK per evidence kind would
            // encode the same fact four ways and still need a constraint saying exactly one is set,
            // and none of them could express the guarantee that actually matters: the id was present
            // in the snapshot that produced the message.
        });

        modelBuilder.Entity<FamiliarActionProposal>(entity =>
        {
            entity.ToTable("FamiliarActionProposals");
            entity.HasKey(proposal => proposal.Id);
            entity.Property(proposal => proposal.Kind).HasConversion<string>().HasMaxLength(32);
            entity.Property(proposal => proposal.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(proposal => proposal.Title).HasMaxLength(FamiliarActionProposal.MaxTitleLength);
            entity.Property(proposal => proposal.RequestedOutcome)
                .HasMaxLength(FamiliarActionProposal.MaxRequestedOutcomeLength);

            // At most one actionable proposal per conversation, which is what makes concurrent
            // confirmation trivially safe for the same reason IX_SessionHandoffs_TaskId_Pending does:
            // contenders can only ever race for one row.
            //
            // The filter matches the stored TEXT because Status uses HasConversion<string>() above.
            // If that conversion is ever removed the filter silently stops matching and the invariant
            // silently disappears — FamiliarProposalPendingUniqueIndexTests guards against it.
            entity.HasIndex(proposal => proposal.ConversationId)
                .IsUnique()
                .HasFilter("\"Status\" = 'Pending'")
                .HasDatabaseName("IX_FamiliarActionProposals_ConversationId_Pending");

            // One created task or session belongs to at most one proposal, so a replayed confirmation
            // can never let two proposals claim the same row. Filtered, because every undecided and
            // dismissed proposal holds NULL.
            entity.HasIndex(proposal => proposal.CreatedTaskId)
                .IsUnique()
                .HasFilter("\"CreatedTaskId\" IS NOT NULL")
                .HasDatabaseName("IX_FamiliarActionProposals_CreatedTaskId");
            entity.HasIndex(proposal => proposal.CreatedSessionId)
                .IsUnique()
                .HasFilter("\"CreatedSessionId\" IS NOT NULL")
                .HasDatabaseName("IX_FamiliarActionProposals_CreatedSessionId");

            // Cascade from the project and from the originating message, not Restrict: deleting a
            // project cascades its conversation and messages, and a Restrict on either FK would abort
            // that cascade when SQLite removed those rows first.
            entity.HasOne(proposal => proposal.Project)
                .WithMany()
                .HasForeignKey(proposal => proposal.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(proposal => proposal.Message)
                .WithMany()
                .HasForeignKey(proposal => proposal.MessageId)
                .OnDelete(DeleteBehavior.Cascade);

            // Restrict on all three task and session links: a proposal describes work a human
            // reviewed, and a task or session it names must never be deleted out from under that
            // record while the proposal still points at it.
            entity.HasOne(proposal => proposal.TargetTask)
                .WithMany()
                .HasForeignKey(proposal => proposal.TargetTaskId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(proposal => proposal.CreatedTask)
                .WithMany()
                .HasForeignKey(proposal => proposal.CreatedTaskId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(proposal => proposal.CreatedSession)
                .WithMany()
                .HasForeignKey(proposal => proposal.CreatedSessionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FamiliarChat>(entity =>
        {
            entity.ToTable("FamiliarChats");
            entity.HasKey(chat => chat.Id);
            entity.Property(chat => chat.Title).HasMaxLength(FamiliarChat.MaxTitleLength).IsRequired();

            // The conversation list is ordered by recent activity, and it is the only list read on
            // every page load of /Familiar.
            entity.HasIndex(chat => chat.UpdatedUtc);

            // SetNull, not Cascade: a conversation is not about its focus project, it merely leans
            // towards it. Deleting the project must lose the lean, never the conversation.
            entity.HasOne(chat => chat.FocusProject)
                .WithMany()
                .HasForeignKey(chat => chat.FocusProjectId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasMany(chat => chat.Turns)
                .WithOne(turn => turn.Chat)
                .HasForeignKey(turn => turn.ChatId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FamiliarChatTurn>(entity =>
        {
            entity.ToTable("FamiliarChatTurns");
            entity.HasKey(turn => turn.Id);
            entity.Property(turn => turn.State).HasConversion<string>().HasMaxLength(32);
            entity.Property(turn => turn.UserText)
                .HasMaxLength(FamiliarChatTurn.MaxUserTextLength)
                .IsRequired();
            entity.Property(turn => turn.Output)
                .HasMaxLength(FamiliarChatTurn.MaxOutputLength)
                .IsRequired();
            entity.Property(turn => turn.FailureCode).HasMaxLength(FamiliarChatTurn.MaxFailureCodeLength);
            entity.Property(turn => turn.ProviderName).HasMaxLength(FamiliarChatTurn.MaxProviderNameLength);
            entity.Property(turn => turn.ProviderModel).HasMaxLength(FamiliarChatTurn.MaxProviderModelLength);
            entity.Property(turn => turn.EvidenceEntryIds).HasMaxLength(FamiliarChatTurn.MaxEvidenceLength);
            entity.Ignore(turn => turn.IsInFlight);

            // Unique, so two racing sends can never produce an ambiguous display order — and so the
            // resume read "everything after sequence N" can never skip or duplicate a turn.
            entity.HasIndex(turn => new { turn.ChatId, turn.Sequence }).IsUnique();

            // At most one turn in flight per conversation, enforced by the database rather than by a
            // check a caller might not run. Same shape and rationale as
            // IX_FamiliarActionProposals_ConversationId_Pending and the single-started-session
            // invariant: contenders can only ever race for one row, so a second sender attaching to
            // the turn already running is the only outcome the schema permits.
            //
            // The filter matches the stored TEXT because State uses HasConversion<string>() above.
            // If that conversion is ever removed the filter silently stops matching and the
            // invariant silently disappears — FamiliarChatInFlightUniqueIndexTests goes red instead.
            entity.HasIndex(turn => turn.ChatId)
                .IsUnique()
                .HasFilter("\"State\" IN ('Pending', 'Generating')")
                .HasDatabaseName("IX_FamiliarChatTurns_ChatId_InFlight");

            // FocusProjectIdAtTime carries no foreign key on purpose. It records what the focus was
            // when the turn was accepted; deleting that project must not rewrite the record of a
            // conversation that already happened.
        });

        modelBuilder.Entity<FamiliarPlanProposal>(entity =>
        {
            entity.ToTable("FamiliarPlanProposals");
            entity.HasKey(plan => plan.Id);
            entity.Property(plan => plan.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(plan => plan.Summary)
                .HasMaxLength(FamiliarPlanProposal.MaxSummaryLength)
                .IsRequired();
            entity.Ignore(plan => plan.IsPending);

            entity.HasOne(plan => plan.Chat)
                .WithMany()
                .HasForeignKey(plan => plan.ChatId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(plan => plan.Turn)
                .WithMany()
                .HasForeignKey(plan => plan.TurnId)
                .OnDelete(DeleteBehavior.Cascade);

            // Restrict, not Cascade: deleting a project must not quietly remove the record of what was
            // proposed for it. The delete fails loudly instead, which is the honest outcome.
            entity.HasOne(plan => plan.Project)
                .WithMany()
                .HasForeignKey(plan => plan.ProjectId)
                .OnDelete(DeleteBehavior.Restrict);

            // At most one undecided plan per conversation. Same shape and rationale as
            // IX_FamiliarActionProposals_ConversationId_Pending and IX_FamiliarChatTurns_ChatId_InFlight:
            // contenders race for one row, a human decides once, and a half-approved plan cannot exist.
            //
            // The filter matches the stored TEXT because Status uses HasConversion<string>() above. If
            // that conversion is ever removed the filter silently stops matching and the invariant
            // silently disappears — FamiliarPlanPendingUniqueIndexTests goes red instead.
            entity.HasIndex(plan => plan.ChatId)
                .IsUnique()
                .HasFilter("\"Status\" = 'Pending'")
                .HasDatabaseName("IX_FamiliarPlanProposals_ChatId_Pending");
        });

        modelBuilder.Entity<FamiliarPlanItem>(entity =>
        {
            entity.ToTable("FamiliarPlanItems");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Title).HasMaxLength(FamiliarPlanItem.MaxTitleLength).IsRequired();
            entity.Property(item => item.RequestedOutcome)
                .HasMaxLength(FamiliarPlanItem.MaxRequestedOutcomeLength)
                .IsRequired();
            entity.Property(item => item.Role).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.EvidenceEntryIds).HasMaxLength(FamiliarPlanItem.MaxEvidenceLength);

            entity.HasOne(item => item.Plan)
                .WithMany(plan => plan.Items)
                .HasForeignKey(item => item.PlanId)
                .OnDelete(DeleteBehavior.Cascade);

            // Stable display order, and unique so two items cannot occupy the same position and make
            // the plan read differently on two devices.
            entity.HasIndex(item => new { item.PlanId, item.Position }).IsUnique();

            // One created task belongs to at most one item, so a replayed approval cannot let two
            // items claim the same task. Filtered, because every unapproved item holds NULL.
            entity.HasIndex(item => item.CreatedTaskId)
                .IsUnique()
                .HasFilter("\"CreatedTaskId\" IS NOT NULL")
                .HasDatabaseName("IX_FamiliarPlanItems_CreatedTaskId");
        });

        modelBuilder.Entity<ContextEntry>(entity =>
        {
            entity.ToTable("ContextEntries");
            entity.HasKey(entry => entry.Id);
            entity.Property(entry => entry.Kind).HasConversion<string>().HasMaxLength(32);
            entity.Property(entry => entry.Title).HasMaxLength(200).IsRequired();
            entity.Property(entry => entry.Content).HasMaxLength(12_000).IsRequired();
            entity.Property(entry => entry.State).HasConversion<string>().HasMaxLength(32);
            entity.Property(entry => entry.Provenance).HasConversion<string>().HasMaxLength(32);
            entity.Property(entry => entry.RecordedBy).HasMaxLength(ContextEntry.MaxRecordedByLength);
            entity.HasIndex(entry => new { entry.ProjectId, entry.TaskId, entry.State, entry.CreatedUtc });
            entity.HasOne(entry => entry.SupersedesContextEntry)
                .WithMany()
                .HasForeignKey(entry => entry.SupersedesContextEntryId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
