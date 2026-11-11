using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace TalentMatch.Infrastructure.Tests.BulkScoring;

public sealed partial class BulkScoringSqlServerConcurrencyTests
{
    [SqlServerFact]
    public async Task Archived_stack_a_survives_stack_b_shared_schema_initialization()
    {
        RequireDisposableSqlServer(Environment.GetEnvironmentVariable(ConnectionVariable)!);
        var archive = Environment.GetEnvironmentVariable("STACK_A_COMPATIBILITY_ARCHIVE")
            ?? throw new InvalidOperationException("A reviewed Stack A ZIP is required.");
        var checksum = Environment.GetEnvironmentVariable("STACK_A_COMPATIBILITY_SHA256")
            ?? throw new InvalidOperationException("The reviewed Stack A ZIP SHA-256 is required.");
        var directory = Path.Combine(Path.GetTempPath(), $"talentmatch-archived-stack-a-{Guid.NewGuid():N}");
        try
        {
            ExtractVerifiedLegacyArchive(archive, checksum, directory);
            await using var fixture = await SqlFixture.CreateAsync(initializeSchema: false);
            await using var db = fixture.Context();
            var connectionString = db.Database.GetConnectionString()!;
            await RunArchivedStackAAsync(directory, connectionString, "seed");

            for (var repeat = 0; repeat < 2; repeat++)
            {
                InitializeHostSchema(db);
                db.ChangeTracker.Clear();
                var job = await db.Jobs.SingleAsync(x => x.Id == "compat-job");
                job.OrganizationId.Should().Be("compat-org");
                job.DepartmentId.Should().Be("compat-dept");
                (await db.JobConfigVersions.SingleAsync(x => x.Id == "compat-config")).RunsPerApplication.Should().Be(5);
                var application = await db.Applications.SingleAsync(x => x.Id == "compat-application");
                application.FinalScore.Should().Be(81);
                application.Status.Should().Be("Completed");
                (await db.Users.SingleAsync(x => x.Id == "compat-admin")).IsActive.Should().BeTrue();
                (await db.Users.SingleAsync(x => x.Id == "compat-disabled")).IsActive.Should().BeFalse();
                var admin = await db.RoleAssignments.SingleAsync(x => x.Id == "compat-bootstrap");
                admin.Source.Should().Be("bootstrap");
                admin.Status.Should().Be("active");
                admin.Role.Should().Be("admin");
                admin.UserObjectId.Should().Be("10000000-0000-4000-8000-000000000001");
                (await db.ProcessingEvents.CountAsync(x => x.Id == "compat-audit")).Should().Be(1);
                (await db.SystemAdministrationConfigurations.CountAsync()).Should().Be(0);

                application.CandidateName = "Updated by Stack B";
                await db.SaveChangesAsync();
                await RunArchivedStackAAsync(directory, connectionString, "verify");
            }

            await RunArchivedStackAAsync(directory, connectionString, "restart");
            InitializeHostSchema(db);
            db.ChangeTracker.Clear();
            (await db.Applications.SingleAsync(x => x.Id == "compat-application")).CandidateName.Should().Be("Updated by Stack B");
            (await db.Users.SingleAsync(x => x.Id == "compat-disabled")).IsActive.Should().BeFalse();
            (await db.RoleAssignments.SingleAsync(x => x.Id == "compat-bootstrap")).Status.Should().Be("active");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("Server=shared.database.windows.net;Database=master")]
    [InlineData("Server=127.0.0.1,1433;Database=application")]
    public void Mixed_version_test_rejects_non_disposable_database_targets(string connectionString)
    {
        var validate = () => RequireDisposableSqlServer(connectionString);
        validate.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Mixed_version_test_rejects_a_changed_archive_before_extraction()
    {
        var directory = Directory.CreateTempSubdirectory("talentmatch-archive-validation-").FullName;
        try
        {
            var archive = Path.Combine(directory, "baseline.zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
                zip.CreateEntry("server/storage/db.js");
            var destination = Path.Combine(directory, "extracted");
            var extract = () => ExtractVerifiedLegacyArchive(archive, new string('0', 64), destination);
            extract.Should().Throw<InvalidOperationException>().WithMessage("*SHA-256*");
            Directory.Exists(destination).Should().BeFalse();
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void RequireDisposableSqlServer(string connectionString)
    {
        var target = new SqlConnectionStringBuilder(connectionString);
        if (!System.Text.RegularExpressions.Regex.IsMatch(target.DataSource, @"^(localhost|127\.0\.0\.1),[0-9]{1,5}$")
            || !string.Equals(target.InitialCatalog, "master", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Mixed-version tests require a disposable loopback SQL Server and the master database.");
    }

    private static void ExtractVerifiedLegacyArchive(string archive, string checksum, string destination)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(checksum, "^[a-fA-F0-9]{64}$"))
            throw new InvalidOperationException("A reviewed SHA-256 checksum is required.");
        using (var stream = File.OpenRead(archive))
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(checksum, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Stack A archive SHA-256 does not match the reviewed package.");
        using (var zip = ZipFile.OpenRead(archive))
        {
            var entry = zip.GetEntry("dist/build-info.json")
                ?? throw new InvalidOperationException("Stack A build metadata is missing.");
            using var stream = entry.Open();
            using var metadata = JsonDocument.Parse(stream);
            var build = metadata.RootElement;
            if (build.GetProperty("authMode").GetString() != "entra"
                || string.IsNullOrWhiteSpace(build.GetProperty("version").GetString())
                || build.TryGetProperty("administrationContract", out _))
                throw new InvalidOperationException("Select the reviewed legacy Entra Stack A package, not the new administration runtime.");
        }
        ZipFile.ExtractToDirectory(archive, destination);
        if (!File.Exists(Path.Combine(destination, "server", "storage", "db.js"))
            || !File.Exists(Path.Combine(destination, "node_modules", "mssql", "package.json")))
            throw new InvalidOperationException("The archived Stack A runtime or its bundled SQL driver is missing.");
    }

    private static async Task RunArchivedStackAAsync(string directory, string connectionString, string operation)
    {
        var start = new ProcessStartInfo("node")
        {
            WorkingDirectory = directory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.Environment.Clear();
        foreach (var key in new[] { "PATH", "SystemRoot", "TEMP", "TMP", "HOME" })
            if (Environment.GetEnvironmentVariable(key) is { } value)
                start.Environment[key] = value;
        start.Environment["AZURE_SQL_CONNECTION_STRING"] = connectionString;
        start.Environment["STORAGE_PROVIDER"] = "azuresql";
        start.ArgumentList.Add(Path.Combine(RepositoryRoot(), "infra", "scripts", "tests", "fixtures", "archived-stack-a-sql-probe.mjs"));
        start.ArgumentList.Add(directory);
        start.ArgumentList.Add(operation);
        using var node = Process.Start(start) ?? throw new InvalidOperationException("The archived Node runtime did not start.");
        var output = node.StandardOutput.ReadToEndAsync();
        var errors = node.StandardError.ReadToEndAsync();
        try
        {
            await node.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3));
            var password = new SqlConnectionStringBuilder(connectionString).Password;
            var diagnostics = (await output + await errors).Replace(connectionString, "[redacted]");
            if (!string.IsNullOrEmpty(password))
                diagnostics = diagnostics.Replace(password, "[redacted]");
            node.ExitCode.Should().Be(0, diagnostics);
            (await output).Should().Contain($"COMPATIBILITY_OK:{operation}");
        }
        finally
        {
            if (!node.HasExited)
            {
                node.Kill(entireProcessTree: true);
                await node.WaitForExitAsync();
            }
        }
    }
}
