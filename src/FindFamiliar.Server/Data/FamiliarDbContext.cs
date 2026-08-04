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

        modelBuilder.Entity<ContextEntry>(entity =>
        {
            entity.ToTable("ContextEntries");
            entity.HasKey(entry => entry.Id);
            entity.Property(entry => entry.Kind).HasConversion<string>().HasMaxLength(32);
            entity.Property(entry => entry.Title).HasMaxLength(200).IsRequired();
            entity.Property(entry => entry.Content).HasMaxLength(12_000).IsRequired();
            entity.Property(entry => entry.State).HasConversion<string>().HasMaxLength(32);
            entity.HasIndex(entry => new { entry.ProjectId, entry.TaskId, entry.State, entry.CreatedUtc });
            entity.HasOne(entry => entry.SupersedesContextEntry)
                .WithMany()
                .HasForeignKey(entry => entry.SupersedesContextEntryId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
