# MYT-3108 notes

## What the agent proposed

The agent turned the search text into a query parameter (`@text`), but it did that by also changing
the product behaviour, and it left the sort injection open. Its 4 tests pass, but they don't prove
the fix, and one of them asserts the regression:

1. **ORDER BY injection via `sortBy` (still open).** It only removed `'` and `;` and then
   put `sortBy` straight into `ORDER BY {column}`. Anything else still goes through: `id`,
   `member_id`, `provider`, `submitted_at`, `amount, (SELECT name FROM members)`, `(SELECT 1)`,
   `CASE WHEN (SELECT 1)=1 THEN amount ELSE status END`, `amount--`. These subqueries and boolean
   conditions in ORDER BY run on the server, which allows blind data extraction (CWE-89 is not
   closed). `amount;DROP TABLE claims` became `amountDROP TABLE claims`, which failed with a SQLite
   syntax error instead of an `ArgumentException`.
2. **Unvalidated `sortDir`.** Any value that wasn't `asc` (null, `ASC; DROP...`, `ascending`, typos)
   was quietly turned into `DESC` instead of being rejected.
3. **Apostrophe stripping.** `text.Replace("'", "")` turns `O'Brien` into `OBrien`, so
   "O'Brien Eye Clinic" can no longer be found. Parameters already make `'` safe, so this is not
   defense in depth. It only corrupts data and breaks FINDING.md.
4. **Exact-match regression.** `LIKE @text` with no `%` around it stops "contains" matching, so
   `dental` no longer finds "Dental cleaning" or "Dental X-ray", and `DENTAL` and provider matches
   break too. That breaks the product rules, which are "unchanged". It isn't even really exact
   match: `%` and `_` typed by the user are still LIKE wildcards, so searching `%` lists **every**
   claim of the member, which is the exact enumeration the agent said it was blocking.
5. **Tests that hide the bug.** `Search_ExactMatchOnly_BlocksWildcardEnumeration` asserts that
   `dental` returns nothing, which goes against FINDING.md. None of its tests touch `sortBy`
   injection, apostrophes or isolation.

## What I changed and why

`src/Claims/ClaimsRepository.cs`:

- **Sort allow-list.** `sortBy` is a lookup key in an ordinal (case-sensitive) dictionary
  `date -> submitted_at`, `amount -> amount`, `status -> status`. `sortDir` maps `asc -> ASC`,
  `desc -> DESC`. Only these fixed literals ever reach the SQL text. Anything else (null, empty,
  `Date`, `date `, column names not in the list, payloads) throws `ArgumentException` **before**
  the command is built. That follows FINDING.md ("anything else is rejected"). A negative `page`
  throws `ArgumentOutOfRangeException`, which is also an `ArgumentException`.
- **Stable paging.** `ORDER BY <col> <dir>, id ASC` adds a fixed tie-breaker so rows with the
  same status or amount don't shift between pages.
- **Contains search, parameter only.** The pattern is `'%' + escaped + '%'`, where the escape
  doubles `\`, then escapes `%` and `_`, and the SQL uses `LIKE @pattern ESCAPE '\'` on
  description OR provider. Wildcards typed by the user are literal (`100%` finds only
  "Copay 100% covered"). Apostrophes are kept (`O'Brien` works). `member_id = @member` stays
  parameterized, so results are always limited to that member. Case-insensitivity comes from
  SQLite's LIKE (ASCII). In production (SQL Server) it depends on the column collation (CI by
  default), so check this on deploy.
- I did **not** change `AgentTests.cs`. `Search_ExactMatchOnly_BlocksWildcardEnumeration` now
  fails on purpose because it asserts behaviour that goes against FINDING.md. It should be removed
  or rewritten once the reviewer agrees (it's flagged in the PR rather than weakened silently).

## Evidence

`tests/Claims.Tests/MyTests.cs`: 32 tests (Theory rows + Facts), all pass on my code:

- Happy: `Search_ContainsMatch_IsCaseInsensitive` (`dental`, `DENTAL`, ... -> 101, 103),
  `Search_MatchesByProvider`, `Search_ApostropheIsKept_FindsOBrien`, `Search_ApostropheIsNotStripped`,
  `Search_EmptyOrNullText_ReturnsAllOfTheMembersClaims`, `Search_EachValidSort_OrdersCorrectly`
  (3 columns x asc/desc), `Search_Paging_Returns20PerPage_AndPage1IsOffsetBy20`.
- Sad/injection (sort): `Search_InvalidSortBy_ThrowsArgumentException_AndTableIsIntact` (16 payloads:
  `id`, `member_id`, `provider`, `submitted_at`, `amount; DROP TABLE claims`, `(SELECT 1)`,
  `CASE WHEN (SELECT 1)=1 THEN amount ELSE status END`, `amount--`, `date `, `Date`, `""`, null, ...),
  `Search_InvalidSortDir_ThrowsArgumentException_AndTableIsIntact` (8 payloads). For every payload
  they also check that the `claims` table still exists with its 6 rows.
- Sad/injection (text): `Search_InjectionInText_ReturnsNoClaims_NoSqlError_TableIntact`
  (`' OR 1=1 --`, `' OR '1'='1`, `') UNION SELECT ... --`, `x'; DROP TABLE claims; --`, ...),
  `Search_WildcardCharacters_AreLiteral` (`%`, `100%`, `_`, `C_ty`, `%%`, `\`, `Copay%`).
- Isolation: `Search_NeverReturnsOtherMembersClaims` (no claim 201 for member 1, even for
  "Bright Smiles", which member 2 also uses), `Search_MatchOnlyInOtherMembersData_ReturnsNothing`.

The payload lists use `Assert.All` inside one `[Fact]` rather than one `[Theory]` row per payload.
Assert.All still names each failing payload in the failure message. The reason:
`check/Program.cs` reads stdout to the end before it reads stderr, and with 45 failing Theory rows
on the agent's version xUnit wrote ~6.1 KB of `[FAIL]` lines to stderr. That filled the pipe buffer
and deadlocked `dotnet run --project check`. With Facts it's ~2.5 KB, and the same inputs are tested.

Against the agent's version (`check/agent_version`), **20 of 32 fail**, for example:

- `Search_InvalidSortBy_...`: with `amount, (SELECT name FROM members)`, `id`, `member_id`,
  `(SELECT 1)`, `CASE WHEN ...`, `Date` or null -> `Assert.Throws() Failure: No exception was thrown`
  (the subquery ran inside ORDER BY: the injection is still open). With `amount; DROP TABLE claims` ->
  `Actual: SqliteException: near "DROP": syntax error` (not an ArgumentException).
- `Search_InvalidSortDir_...` -> no exception; bad values silently became DESC.
- `Search_ContainsMatch_IsCaseInsensitive` (all 4), `Search_MatchesByProvider`,
  `Search_ApostropheIsKept_FindsOBrien` (all 3) -> returned nothing (exact match + apostrophe stripping).
- `Search_WildcardCharacters_AreLiteral`: `%` returned all 5 claims instead of only 104.
- `Search_EachValidSort_OrdersCorrectly` (all 6), `Search_Paging_...`, `Search_EmptyOrNullText_...`
  -> empty results because of the exact-match regression.

The 12 that still pass on the agent's version are the text-injection and isolation cases, which the
parameterized `@member`/`@text` already handled. They stay as regression tests.

### Validation outputs

`dotnet test tests/Claims.Tests`:

```
[xUnit.net] Claims.Tests.AgentTests.Search_ExactMatchOnly_BlocksWildcardEnumeration [FAIL]
Failed!  - Failed: 1, Passed: 35, Skipped: 0, Total: 36 - Claims.Tests.dll (net8.0)
```

The only failure is the agent's `Search_ExactMatchOnly_BlocksWildcardEnumeration`: it asserts that
`dental` returns nothing, which goes against FINDING.md ("`dental` finds Dental cleaning and Dental X-ray").
I left it unchanged for review.

`dotnet run --project check`:

```
T1 Sorting: 4/4 checks pass
T2 Search: 4/4 checks pass
   Extra: "100%" finds only the claim that really contains 100%: yes
T3 Your tests: 32 tests | pass on your code: 32/32 | catch the agent's version: 20 of 32
T4 Notes: 3/3 sections written

The agent's tests on your code: 3 passed, 1 failed
Check files: c0cd69d4
```
