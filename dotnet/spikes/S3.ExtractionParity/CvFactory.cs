using System.Text;
using S2.PdfiumBenchmark;

namespace S3.ExtractionParity;

// A synthetic CV with known facts, so extraction can be scored without a human reading 40 documents.
internal sealed record Cv(int Id, int Pages, byte[] Pdf, IReadOnlyList<(string Category, string Fact)> Facts);

internal static class CvFactory
{
    private const int LinesPerPage = 54;
    private const int Width = 92;

    private static readonly string[] First = ["Alvaro", "Brenna", "Casimir", "Dalia", "Emeric", "Fenella", "Gustavo", "Halina", "Ivor", "Jovita", "Kasimir", "Linnea", "Matteo", "Nerys", "Osvaldo", "Perpetua", "Quillon", "Rosalind", "Stellan", "Thessaly"];
    private static readonly string[] Last = ["Ashgrove", "Brennigan", "Calloway", "Dunmore", "Eastwick", "Fairlight", "Garrowby", "Hartnell", "Ironside", "Jessamy", "Kingsford", "Lockhart", "Marlowe", "Northcott", "Oakhaven", "Pemberton", "Quenby", "Ravenscar", "Stonebridge", "Tavistock"];
    private static readonly string[] Stem = ["Harbor", "Quartz", "Willow", "Granite", "Lantern", "Meridian", "Summit", "Cobalt", "Juniper", "Beacon", "Saffron", "Falcon"];
    private static readonly string[] Kind = ["Systems", "Labs", "Logistics", "Analytics", "Networks", "Robotics", "Financial", "Health"];
    private static readonly string[] Titles = ["Software Engineer", "Senior Developer", "Data Analyst", "Project Manager", "DevOps Engineer", "Solutions Architect", "QA Lead", "Product Owner", "Security Analyst", "Platform Engineer"];
    private static readonly string[] Skills = ["Kubernetes", "PostgreSQL", "Terraform", "TypeScript", "Python", "Kafka", "Redis", "GraphQL", "Docker", "Prometheus", "Ansible", "Snowflake", "Spark", "Jenkins", "Elasticsearch", "RabbitMQ", "gRPC", "Airflow", "Selenium", "Grafana"];
    private static readonly string[] Degrees = ["BSc Computer Science", "MSc Data Engineering", "BEng Electronic Engineering", "MBA Technology Management", "BA Mathematics"];
    private static readonly string[] Schools = ["University of Wexford", "Hollis Institute of Technology", "Dunmere College", "Northgate University", "Saint Aldric University"];
    private static readonly string[] Certs = ["AWS Solutions Architect Associate", "Certified Kubernetes Administrator", "PMP Certification", "Azure Administrator Associate", "Scrum Master Certification", "CISSP"];
    private static readonly string[] Things = ["build time", "deployment time", "infrastructure cost", "incident volume", "page load time", "report turnaround", "onboarding time", "defect rate"];
    private static readonly string[] Verbs = ["Led", "Designed", "Delivered", "Automated", "Migrated", "Rebuilt", "Introduced", "Standardised"];
    private static readonly string[] Objects = ["the order processing platform", "a customer analytics pipeline", "the internal billing service", "a multi-region deployment process", "the monitoring and alerting stack", "a data quality framework", "the partner integration gateway", "a self-service reporting portal"];

    public static Cv Create(int id, int targetPages)
    {
        var rnd = new Random(7000 + id);
        T Pick<T>(IReadOnlyList<T> list) => list[rnd.Next(list.Count)];
        var facts = new List<(string, string)>();
        var lines = new List<string>();

        var first = First[id % First.Length];
        var last = Last[(id * 7 + 3) % Last.Length];
        var name = $"{first} {last}";
        var email = $"{first}.{last}{id}@example.test".ToLowerInvariant();
        var phone = $"+44 7700 9{rnd.Next(10000, 99999)}";
        facts.Add(("name", name));
        facts.Add(("email", email));
        facts.Add(("phone", phone));

        lines.Add(name.ToUpperInvariant());
        lines.Add($"{Pick(Titles)}  |  {email}  |  {phone}");
        lines.Add("");
        lines.Add("PROFILE");
        lines.AddRange(Wrap($"Hands-on engineer with {rnd.Next(6, 20)} years of experience delivering reliable software for regulated industries, with a focus on automation, measurable outcomes and mentoring."));
        lines.Add("");
        lines.Add("SKILLS");
        var skills = Enumerable.Range(0, Skills.Length).OrderBy(_ => rnd.Next()).Take(8).Select(i => Skills[i]).ToList();
        foreach (var skill in skills) facts.Add(("skill", skill));
        lines.AddRange(Wrap(string.Join(", ", skills)));
        lines.Add("");

        var year = 2024;
        var used = new HashSet<string>();
        string Company()
        {
            string c;
            do c = $"{Pick(Stem)} {Pick(Kind)}"; while (!used.Add(c));
            return c;
        }

        lines.Add("EXPERIENCE");
        var jobIndex = 0;
        // Add jobs until the document is long enough for the target page count.
        while ((lines.Count < targetPages * LinesPerPage - 24 || jobIndex < 2) && jobIndex <= 80)
        {
            var company = Company();
            var title = Pick(Titles);
            var end = year;
            var start = year - rnd.Next(2, 5);
            year = start - 1;
            facts.Add(("company", company));
            facts.Add(("title", $"{title}, {company}"));
            facts.Add(("period", $"{start} - {end}"));
            lines.Add($"{title}, {company}   {start} - {end}");
            for (var b = 0; b < rnd.Next(3, 6); b++)
            {
                var thing = Pick(Things);
                var pct = rnd.Next(12, 88);
                lines.AddRange(Wrap($"- {Pick(Verbs)} {Pick(Objects)}; reduced {thing} by {pct}% across {rnd.Next(3, 40)} teams."));
                facts.Add(("metric", $"reduced {thing} by {pct}"));
            }
            lines.Add("");
            jobIndex++;
        }

        lines.Add("EDUCATION");
        var degree = Pick(Degrees);
        var school = Pick(Schools);
        facts.Add(("degree", degree));
        facts.Add(("school", school));
        lines.Add($"{degree}, {school}   {year - rnd.Next(1, 4)}");
        lines.Add("");
        lines.Add("CERTIFICATIONS");
        var cert = Pick(Certs);
        facts.Add(("certification", cert));
        lines.Add(cert);

        var pages = (int)Math.Ceiling(lines.Count / (double)LinesPerPage);
        return new Cv(id, pages, Pdf(lines), facts);
    }

    private static IEnumerable<string> Wrap(string text)
    {
        var line = new StringBuilder();
        foreach (var word in text.Split(' '))
        {
            if (line.Length + word.Length + 1 > Width) { yield return line.ToString(); line.Clear(); }
            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }
        if (line.Length > 0) yield return line.ToString();
    }

    private static byte[] Pdf(List<string> lines)
    {
        var pdf = new RawPdf();
        var pageIds = new List<int>();
        for (var start = 0; start < lines.Count; start += LinesPerPage)
        {
            var content = new StringBuilder("BT /F1 10 Tf 12 TL 50 750 Td\n");
            foreach (var line in lines.Skip(start).Take(LinesPerPage))
            {
                var heading = line.Length > 0 && line == line.ToUpperInvariant() && line.All(c => char.IsLetter(c) || c == ' ');
                if (heading) content.Append("/F1 12 Tf\n");
                content.Append('(').Append(line.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)")).Append(") Tj T*\n");
                if (heading) content.Append("/F1 10 Tf\n");
            }
            content.Append("ET\n");
            pageIds.Add(pdf.AddPage(612, 792, content.ToString(), 0));
        }
        return pdf.Build(pageIds);
    }
}
