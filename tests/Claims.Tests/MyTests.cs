using Microsoft.Data.Sqlite;

namespace Claims.Tests;

// MYT-3108: tests for the claims search, based on FINDING.md.
public class MyTests
{
    private static ClaimsRepository Repo() => new(Db.CreateSeeded());

    private static long[] Ids(IEnumerable<Claim> claims) => claims.Select(c => c.Id).ToArray();

    private static bool ClaimsTableExists(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'claims'";
        return (long)cmd.ExecuteScalar()! == 1;
    }

    private static long ClaimsRowCount(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM claims";
        return (long)cmd.ExecuteScalar()!;
    }

    // ---------- Happy path: search ----------

    [Theory]
    [InlineData("dental")]
    [InlineData("DENTAL")]
    [InlineData("Dental")]
    [InlineData("dEnTaL")]
    public void Search_ContainsMatch_IsCaseInsensitive(string text)
    {
        // Matches description "Dental cleaning"/"Dental X-ray" and provider "Bright Smiles Dental".
        Assert.Equal(new long[] { 101, 103 }, Ids(Repo().Search(1, text)).OrderBy(x => x));
    }

    [Fact]
    public void Search_MatchesByProvider()
    {
        Assert.Equal(new long[] { 104, 105 }, Ids(Repo().Search(1, "pharmacy")).OrderBy(x => x));
    }

    [Theory]
    [InlineData("O'Brien")]
    [InlineData("o'brien")]
    [InlineData("'Brien Eye")]
    public void Search_ApostropheIsKept_FindsOBrien(string text)
    {
        Assert.Equal(new long[] { 102 }, Ids(Repo().Search(1, text)));
    }

    [Fact]
    public void Search_ApostropheIsNotStripped()
    {
        // The agent removed apostrophes, so "O'Brien" turned into "OBrien" and matched nothing.
        // "OBrien" is not in the data: it must not match.
        Assert.Empty(Repo().Search(1, "OBrien"));
    }

    [Fact]
    public void Search_EmptyOrNullText_ReturnsAllOfTheMembersClaims()
    {
        Assert.Equal(new long[] { 101, 102, 103, 104, 105 }, Ids(Repo().Search(1, "")).OrderBy(x => x));
        Assert.Equal(new long[] { 101, 102, 103, 104, 105 }, Ids(Repo().Search(1, null)).OrderBy(x => x));
    }

    // ---------- Happy path: sorting ----------

    [Theory]
    [InlineData("date", "asc", new long[] { 101, 102, 103, 104, 105 })]
    [InlineData("date", "desc", new long[] { 105, 104, 103, 102, 101 })]
    [InlineData("amount", "asc", new long[] { 104, 103, 102, 105, 101 })]
    [InlineData("amount", "desc", new long[] { 101, 105, 102, 103, 104 })]
    [InlineData("status", "asc", new long[] { 105, 101, 102, 104, 103 })]  // denied, paid x3 (by id), pending
    [InlineData("status", "desc", new long[] { 103, 101, 102, 104, 105 })] // pending, paid x3 (by id), denied
    public void Search_EachValidSort_OrdersCorrectly(string sortBy, string sortDir, long[] expected)
    {
        Assert.Equal(expected, Ids(Repo().Search(1, "", sortBy, sortDir)));
    }

    [Fact]
    public void Search_Paging_Returns20PerPage_AndPage1IsOffsetBy20()
    {
        var db = Db.CreateSeeded();
        using (var cmd = db.CreateCommand())
        {
            // 25 more claims for member 1 (30 in total) and 5 for member 2.
            for (var i = 0; i < 25; i++)
                cmd.CommandText += $"INSERT INTO claims VALUES ({1000 + i}, 1, 'Paging Clinic', 'Visit {i}', {i}, 'paid', '2026-11-{(i % 28) + 1:00}');";
            for (var i = 0; i < 5; i++)
                cmd.CommandText += $"INSERT INTO claims VALUES ({2000 + i}, 2, 'Paging Clinic', 'Visit {i}', {i}, 'paid', '2026-11-01');";
            cmd.ExecuteNonQuery();
        }
        var repo = new ClaimsRepository(db);

        var page0 = Ids(repo.Search(1, "", "amount", "asc", page: 0));
        var page1 = Ids(repo.Search(1, "", "amount", "asc", page: 1));
        var page2 = Ids(repo.Search(1, "", "amount", "asc", page: 2));
        var all = page0.Concat(page1).ToArray();

        Assert.Equal(20, page0.Length);
        Assert.Equal(10, page1.Length);
        Assert.Empty(page2);
        Assert.Empty(page0.Intersect(page1));
        Assert.Equal(30, all.Distinct().Count());
        Assert.DoesNotContain(all, id => id >= 2000 || (id >= 200 && id < 300));
    }

    // ---------- Sad path / injection: sorting ----------

    // Payload lists use Assert.All instead of [Theory] rows: every failing payload is still named in the
    // failure message, but there is one failing entry per test. (check/Program.cs reads stdout before stderr,
    // and dozens of [FAIL] lines on stderr deadlock it when these tests run on the agent's version.)
    private static readonly string?[] InvalidSortBy =
    {
        "id", "member_id", "provider", "submitted_at",
        "amount; DROP TABLE claims", "amount;DROP TABLE claims", "amount, (SELECT name FROM members)",
        "(SELECT 1)", "CASE WHEN (SELECT 1)=1 THEN amount ELSE status END", "amount--",
        "date ", " date", "Date", "AMOUNT", "", null,
    };

    private static readonly string?[] InvalidSortDir =
    {
        "ASC", "Desc", "asc ", "ascending", "asc; DROP TABLE claims", "asc, (SELECT name FROM members)", "", null,
    };

    [Fact]
    public void Search_InvalidSortBy_ThrowsArgumentException_AndTableIsIntact()
    {
        Assert.All(InvalidSortBy, sortBy =>
        {
            var db = Db.CreateSeeded();
            var repo = new ClaimsRepository(db);

            Assert.Throws<ArgumentException>(() => repo.Search(1, "City Pharmacy", sortBy, "asc"));
            Assert.True(ClaimsTableExists(db));
            Assert.Equal(6, ClaimsRowCount(db));
        });
    }

    [Fact]
    public void Search_InvalidSortDir_ThrowsArgumentException_AndTableIsIntact()
    {
        Assert.All(InvalidSortDir, sortDir =>
        {
            var db = Db.CreateSeeded();
            var repo = new ClaimsRepository(db);

            Assert.Throws<ArgumentException>(() => repo.Search(1, "City Pharmacy", "amount", sortDir));
            Assert.True(ClaimsTableExists(db));
            Assert.Equal(6, ClaimsRowCount(db));
        });
    }

    // ---------- Sad path / injection: search text ----------

    [Theory]
    [InlineData("' OR 1=1 --")]
    [InlineData("' OR '1'='1")]
    [InlineData("%' OR member_id = 2 --")]
    [InlineData("') UNION SELECT id, provider, description, amount, status, submitted_at FROM claims --")]
    [InlineData("x'; DROP TABLE claims; --")]
    [InlineData("\" OR \"\"=\"")]
    public void Search_InjectionInText_ReturnsNoClaims_NoSqlError_TableIntact(string text)
    {
        var db = Db.CreateSeeded();
        var repo = new ClaimsRepository(db);

        var results = repo.Search(1, text); // must not throw a SQL error

        Assert.Empty(results); // no claim really contains these strings
        Assert.True(ClaimsTableExists(db));
        Assert.Equal(6, ClaimsRowCount(db));
    }

    [Fact]
    public void Search_WildcardCharacters_AreLiteral()
    {
        var cases = new (string Text, long[] Expected)[]
        {
            ("%", new long[] { 104 }),      // only "Copay 100% covered" really contains %
            ("100%", new long[] { 104 }),   // not "Copay 100 dollars"
            ("_", new long[0]),             // no claim contains an underscore
            ("C_ty", new long[0]),          // _ is not a single-character wildcard
            ("%%", new long[0]),
            ("\\", new long[0]),
            ("Copay%", new long[0]),
        };

        Assert.All(cases, c => Assert.Equal(c.Expected, Ids(Repo().Search(1, c.Text))));
    }

    // ---------- Isolation ----------

    [Theory]
    [InlineData("Bright Smiles")]   // provider shared with member 2's claim 201
    [InlineData("dental")]
    [InlineData("")]
    [InlineData("%")]
    [InlineData("' OR 1=1 --")]
    public void Search_NeverReturnsOtherMembersClaims(string text)
    {
        Assert.DoesNotContain(Repo().Search(1, text), c => c.Id == 201);
    }

    [Fact]
    public void Search_MatchOnlyInOtherMembersData_ReturnsNothing()
    {
        Assert.Empty(Repo().Search(1, "crown"));                       // only member 2 has a crown
        Assert.Equal(new long[] { 201 }, Ids(Repo().Search(2, "dental")));
    }
}
