using Microsoft.EntityFrameworkCore;

namespace MateMCP.Api.Data;

public static class DatabaseSchemaUpgrade
{
    public static async Task EnsureExternalLoginsAsync(
        ControlPlaneDbContext db,
        string provider,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(provider, "sqlite", StringComparison.OrdinalIgnoreCase))
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE IF NOT EXISTS "ExternalLogins" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_ExternalLogins" PRIMARY KEY,
                    "UserAccountId" TEXT NOT NULL,
                    "Provider" TEXT NOT NULL,
                    "ProviderKey" TEXT NOT NULL,
                    "Email" TEXT NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    CONSTRAINT "FK_ExternalLogins_Users_UserAccountId"
                        FOREIGN KEY ("UserAccountId") REFERENCES "Users" ("Id") ON DELETE CASCADE
                );
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_ExternalLogins_Provider_ProviderKey"
                    ON "ExternalLogins" ("Provider", "ProviderKey");
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_ExternalLogins_UserAccountId_Provider"
                    ON "ExternalLogins" ("UserAccountId", "Provider");
                """,
                cancellationToken);
            return;
        }

        if (string.Equals(provider, "sqlserver", StringComparison.OrdinalIgnoreCase))
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                IF OBJECT_ID(N'[ExternalLogins]', N'U') IS NULL
                BEGIN
                    CREATE TABLE [ExternalLogins] (
                        [Id] uniqueidentifier NOT NULL,
                        [UserAccountId] uniqueidentifier NOT NULL,
                        [Provider] nvarchar(64) NOT NULL,
                        [ProviderKey] nvarchar(512) NOT NULL,
                        [Email] nvarchar(320) NOT NULL,
                        [CreatedAt] datetimeoffset NOT NULL,
                        CONSTRAINT [PK_ExternalLogins] PRIMARY KEY ([Id]),
                        CONSTRAINT [FK_ExternalLogins_Users_UserAccountId]
                            FOREIGN KEY ([UserAccountId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE
                    );
                END;

                IF NOT EXISTS (
                    SELECT 1 FROM sys.indexes
                    WHERE [name] = N'IX_ExternalLogins_Provider_ProviderKey'
                      AND [object_id] = OBJECT_ID(N'[ExternalLogins]')
                )
                    CREATE UNIQUE INDEX [IX_ExternalLogins_Provider_ProviderKey]
                        ON [ExternalLogins] ([Provider], [ProviderKey]);

                IF NOT EXISTS (
                    SELECT 1 FROM sys.indexes
                    WHERE [name] = N'IX_ExternalLogins_UserAccountId_Provider'
                      AND [object_id] = OBJECT_ID(N'[ExternalLogins]')
                )
                    CREATE UNIQUE INDEX [IX_ExternalLogins_UserAccountId_Provider]
                        ON [ExternalLogins] ([UserAccountId], [Provider]);
                """,
                cancellationToken);
            return;
        }

        throw new InvalidOperationException("Unsupported database provider for schema upgrade.");
    }
}
