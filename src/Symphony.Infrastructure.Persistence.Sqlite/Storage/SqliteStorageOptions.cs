namespace Symphony.Infrastructure.Persistence.Sqlite.Storage;

public sealed class SqliteStorageOptions
{
    public const string SectionName = "Persistence";

    public string ConnectionString { get; init; } = "Data Source=./data/symphony.db;Cache=Shared;Mode=ReadWriteCreate";
}
