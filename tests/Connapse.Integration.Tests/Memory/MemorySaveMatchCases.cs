namespace Connapse.Integration.Tests.Memory;

/// <summary>
/// The labelled cases CON-332's pass bar (<c>eval/pass-bars.md</c>) judges the save check on, written with the bar and
/// before the check first ran. Each case seeds a space with <see cref="Background"/> and its own facts, saves one fact,
/// and names the seeded facts (by index into <see cref="MatchCase.Seeded"/>) the save must show.
/// </summary>
public static class MemorySaveMatchCases
{
    public enum Family { Same, RewordedAttribute, SimilarSubject, Unrelated, SynonymSubject }

    public sealed record Fact(string Subject, string Attribute, string Value);

    public sealed record MatchCase(string Name, Family Family, Fact[] Seeded, Fact Saved, int[] Expected)
    {
        public override string ToString() => Name;
    }

    /// <summary>Facts every case's space holds, the way a project manager's memory looks.</summary>
    public static readonly Fact[] Background =
    [
        new("Halcyon", "kind", "billing service"),
        new("Halcyon", "repo", "orrin/halcyon"),
        new("Wicket", "kind", "SSO gateway"),
        new("Wicket", "repo", "orrin/wicket"),
        new("Gannet", "kind", "freezer monitor"),
        new("Gannet", "repo", "orrin/gannet"),
        new("Ferro", "kind", "job runner"),
        new("Ferro", "repo", "orrin/ferro"),
        new("Orrin Systems", "kind", "company"),
        new("Priya Natarajan", "team", "Platform"),
        new("Tom Okafor", "team", "Data"),
        new("Mei Lin", "role", "SRE"),
        new("Platform team", "slack channel", "#platform"),
        new("Data team", "manager", "Sam Cole"),
        new("CON-280", "status", "Done"),
        new("CON-285", "status", "In progress"),
        new("CON-288", "owner", "Mei Lin"),
        new("eu-west-1", "provider", "AWS"),
        new("Release 0.4.2", "date", "2026-11-20"),
    ];

    private static MatchCase C(string name, Family family, Fact saved, int[] expected, params Fact[] seeded) =>
        new(name, family, seeded, saved, expected);

    private static Fact F(string subject, string attribute, string value) => new(subject, attribute, value);

    public static readonly MatchCase[] All =
    [
        // Same subject and attribute.
        C("ticket status saved twice", Family.Same, F("CON-291", "status", "Merged"), [0], F("CON-291", "status", "In review")),
        C("case and spacing differ", Family.Same, F("halcyon", "database ", "PostgreSQL 16"), [0], F("Halcyon", "Database", "PostgreSQL 15")),
        C("new owner", Family.Same, F("Wicket", "Owner", "Ravi Banerjee"), [0], F("Wicket", "owner", "Dana Whitlock")),
        C("ticket id punctuated differently", Family.Same, F("CON 291", "Status", "Done"), [0], F("con-291", "status", "Blocked")),
        C("new scheduler", Family.Same, F("Ferro", "scheduler", "Quartz.NET"), [0], F("Ferro", "scheduler", "Hangfire")),
        C("same value again", Family.Same, F("Gannet", "deploy target", "eu-west-1"), [0], F("Gannet", "deploy target", "eu-west-1")),
        C("release date moved", Family.Same, F("release 0.5.0", "date", "2026-12-08"), [0], F("Release 0.5.0", "date", "2026-12-01")),
        C("person's new role", Family.Same, F("Dana Whitlock", "role", "Engineering manager"), [0], F("Dana Whitlock", "role", "Tech lead")),

        // Same subject, attribute reworded.
        C("status saved as current status", Family.RewordedAttribute, F("CON-291", "current status", "Merged"), [0], F("CON-291", "status", "In review")),
        C("status saved as state", Family.RewordedAttribute, F("CON-291", "state", "Merged"), [0], F("CON-291", "status", "In review")),
        C("deploy target as deployment target", Family.RewordedAttribute, F("Halcyon", "deployment target", "us-east-2"), [0], F("Halcyon", "deploy target", "eu-west-1")),
        C("owner as owned by", Family.RewordedAttribute, F("Wicket", "owned by", "Ravi Banerjee"), [0], F("Wicket", "owner", "Dana Whitlock")),
        C("database as database version", Family.RewordedAttribute, F("Halcyon", "database version", "PostgreSQL 16"), [0], F("Halcyon", "database", "PostgreSQL 15")),
        C("job scheduler as scheduler", Family.RewordedAttribute, F("Ferro", "scheduler", "Quartz"), [0], F("Ferro", "job scheduler", "Hangfire")),
        C("tech lead as lead engineer", Family.RewordedAttribute, F("Gannet", "lead engineer", "Tom Okafor"), [0], F("Gannet", "tech lead", "Priya Natarajan")),
        C("on-call rotation as oncall", Family.RewordedAttribute, F("Halcyon", "oncall", "Team Red"), [0], F("Halcyon", "on-call rotation", "Team Blue")),
        C("session length as session timeout", Family.RewordedAttribute, F("Wicket", "session timeout", "12 hours"), [0], F("Wicket", "session length", "8 hours")),
        C("database as db engine, same value reworded", Family.RewordedAttribute, F("Halcyon", "db engine", "Postgres 15"), [0], F("Halcyon", "database", "PostgreSQL 15")),

        // Subject spelled or extended differently.
        C("subject with a suffix", Family.SimilarSubject, F("Halcyon API", "database", "PostgreSQL 16"), [0], F("Halcyon", "database", "PostgreSQL 15")),
        C("subject misspelled", Family.SimilarSubject, F("Halycon", "owner", "Mei Lin"), [0], F("Halcyon", "owner", "Ravi Banerjee")),
        C("shorter subject", Family.SimilarSubject, F("Wicket", "owner", "Ravi Banerjee"), [0], F("Wicket SSO gateway", "owner", "Dana Whitlock")),
        C("subject without its prefix", Family.SimilarSubject, F("Gannet", "tech lead", "Tom Okafor"), [0], F("Project Gannet", "tech lead", "Priya Natarajan")),
        C("ticket with its title", Family.SimilarSubject, F("CON-291 memory spaces", "status", "Merged"), [0], F("CON-291", "status", "In review")),
        C("hyphenated subject", Family.SimilarSubject, F("Ferro scheduler", "runtime", "dotnet 10"), [0], F("ferro-scheduler", "runtime", "dotnet 8")),
        C("shorter subject, attribute synonym", Family.SimilarSubject, F("Orrin", "chief executive", "Sam Cole"), [0], F("Orrin Systems", "CEO", "Lena Ward")),

        // Nothing close: must show nothing.
        C("another ticket's status", Family.Unrelated, F("CON-292", "status", "Merged"), [], F("CON-291", "status", "In review")),
        C("same subject, other attribute", Family.Unrelated, F("Halcyon", "owner", "Ravi Banerjee"), [], F("Halcyon", "database", "PostgreSQL 15")),
        C("other subject, same attribute", Family.Unrelated, F("Gannet", "database", "MySQL 8"), [], F("Halcyon", "database", "PostgreSQL 15")),
        C("next release's date", Family.Unrelated, F("Release 0.5.1", "date", "2027-01-10"), [], F("Release 0.5.0", "date", "2026-12-01")),
        C("other subject, same value", Family.Unrelated, F("Wicket", "cache", "Redis 7"), [], F("Halcyon", "cache", "Redis 7")),
        C("a new person", Family.Unrelated, F("Ana Souza", "team", "Platform"), []),
        C("same subject, new attribute", Family.Unrelated, F("Ferro", "language", "C#"), [], F("Ferro", "scheduler", "Hangfire")),
        C("another ticket's owner", Family.Unrelated, F("CON-301", "owner", "Dana Whitlock"), [], F("CON-291", "owner", "Dana Whitlock")),
        C("other subject, same region", Family.Unrelated, F("Halcyon", "region", "eu-west-1"), [], F("Gannet", "deploy target", "eu-west-1")),
        C("status of a service", Family.Unrelated, F("Wicket", "status", "GA"), [], F("CON-291", "status", "In review")),
        C("launch date beside freeze date", Family.Unrelated, F("Halcyon", "launch date", "2027-01-19"), [], F("Halcyon", "freeze date", "2027-03-02")),
        C("owner beside tech lead", Family.Unrelated, F("Gannet", "owner", "Tom Okafor"), [], F("Gannet", "tech lead", "Priya Natarajan")),
        C("a product named in a value", Family.Unrelated, F("PostgreSQL", "license", "PostgreSQL License"), [], F("Halcyon", "database", "PostgreSQL 15")),

        // The subject named in other words: reported, not gated.
        C("freezer monitor as cold-room watcher", Family.SynonymSubject, F("cold-room watcher", "database", "InfluxDB"), [0], F("Gannet", "database", "TimescaleDB")),
        C("Wicket as SSO gateway", Family.SynonymSubject, F("SSO gateway", "owner", "Ravi Banerjee"), [0], F("Wicket", "owner", "Dana Whitlock")),
        C("Halcyon as billing service", Family.SynonymSubject, F("billing service", "database", "PostgreSQL 16"), [0], F("Halcyon", "database", "PostgreSQL 15")),
        C("Ferro as the batch runner", Family.SynonymSubject, F("batch runner", "owner", "Sam Cole"), [0], F("Ferro", "owner", "Mei Lin")),
        C("Orrin Systems as the firm", Family.SynonymSubject, F("the firm", "CEO", "Sam Cole"), [0], F("Orrin Systems", "CEO", "Lena Ward")),
    ];
}
