using System.Text.Json;
using HhScout.Core;
using Microsoft.Data.Sqlite;

namespace HhScout.Api;

public sealed class Storage
{
    private readonly string connectionString;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Storage(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 10 }.ToString();
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS vacancies (id TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS companies (id TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS vacancy_archive (id TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS vacancy_details (id TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS cover_letters (id TEXT PRIMARY KEY, vacancy_id TEXT NOT NULL, json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS cover_letters_vacancy ON cover_letters(vacancy_id);
            INSERT OR IGNORE INTO vacancy_archive SELECT id,json FROM vacancies;
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open() { var db = new SqliteConnection(connectionString); db.Open(); return db; }

    public T? Read<T>(string key)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT json FROM settings WHERE key=$key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<T>(json, Json) : default;
    }

    public void Save<T>(string key, T value)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO settings VALUES($key,$json) ON CONFLICT(key) DO UPDATE SET json=excluded.json";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(value, Json));
        cmd.ExecuteNonQuery();
    }

    public Vacancy[] Vacancies() => All<Vacancy>("vacancies");
    public Dictionary<string, CompanyInfo> Companies() => All<CompanyInfo>("companies").ToDictionary(x => x.EmployerId);

    private T[] All<T>(string table)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT json FROM {table}";
        using var reader = cmd.ExecuteReader();
        var result = new List<T>();
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), Json)!);
        return result.ToArray();
    }

    // An unsuccessful fetch keeps the last good snapshot; replace data and metadata in one transaction.
    public void Replace(Vacancy[] vacancies, SearchSettings search, DateTimeOffset syncedAt, int found, bool truncated)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var clear = db.CreateCommand();
        clear.Transaction = tx;
        clear.CommandText = "DELETE FROM vacancies";
        clear.ExecuteNonQuery();
        foreach (var v in vacancies.DistinctBy(x => x.Id))
        {
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO vacancies VALUES($id,$json); INSERT INTO vacancy_archive VALUES($id,$json) ON CONFLICT(id) DO UPDATE SET json=excluded.json";
            cmd.Parameters.AddWithValue("$id", v.Id);
            cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(v, Json));
            cmd.ExecuteNonQuery();
        }
        using var meta = db.CreateCommand();
        meta.Transaction = tx;
        meta.CommandText = "INSERT INTO settings VALUES('snapshot',$json) ON CONFLICT(key) DO UPDATE SET json=excluded.json";
        meta.Parameters.AddWithValue("$json", JsonSerializer.Serialize(new SnapshotInfo(search, syncedAt, found, truncated), Json));
        meta.ExecuteNonQuery();
        tx.Commit();
    }

    public void SaveCompany(CompanyInfo company)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO companies VALUES($id,$json) ON CONFLICT(id) DO UPDATE SET json=excluded.json";
        cmd.Parameters.AddWithValue("$id", company.EmployerId);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(company, Json));
        cmd.ExecuteNonQuery();
    }

    private T? Find<T>(string table, string id)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT json FROM {table} WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<T>(json, Json) : default;
    }
    private void Upsert<T>(string table, string id, T value)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = $"INSERT INTO {table}(id,json) VALUES($id,$json) ON CONFLICT(id) DO UPDATE SET json=excluded.json";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(value, Json));
        cmd.ExecuteNonQuery();
    }
    public Vacancy? ArchivedVacancy(string id) => Find<Vacancy>("vacancy_archive", id);
    public VacancyDetails? Details(string id) => Find<VacancyDetails>("vacancy_details", id);
    public void SaveDetails(VacancyDetails details) => Upsert("vacancy_details", details.VacancyId, details);
    public CoverLetter? Letter(string id) => Find<CoverLetter>("cover_letters", id);
    public CoverLetter[] Letters(string vacancyId)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT json FROM cover_letters WHERE vacancy_id=$id";
        cmd.Parameters.AddWithValue("$id", vacancyId);
        using var reader = cmd.ExecuteReader(); var result = new List<CoverLetter>();
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<CoverLetter>(reader.GetString(0), Json)!);
        return result.OrderByDescending(x => x.CreatedAt).ToArray();
    }
    public void SaveLetter(CoverLetter letter)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO cover_letters VALUES($id,$vacancy,$json) ON CONFLICT(id) DO UPDATE SET json=excluded.json";
        cmd.Parameters.AddWithValue("$id", letter.Id); cmd.Parameters.AddWithValue("$vacancy", letter.VacancyId);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(letter, Json)); cmd.ExecuteNonQuery();
    }
}

public sealed record SnapshotInfo(SearchSettings Search, DateTimeOffset SyncedAt, int Found, bool Truncated);
