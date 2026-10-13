namespace TechCare.Clinic.Api.Features.Ai;

/// <summary>
/// The kind of evidence a library file holds. It is decided from the file name, so every chunk of a file
/// carries the same label and a question can be searched only in the sources that could answer it — a dose
/// question never has to compete with a 2024 PubMed abstract.
/// </summary>
public static class LibrarySourceTypes
{
    public const string DrugLabel = "drug_label";
    public const string Guideline = "guideline";
    public const string EgyptCatalog = "egypt_catalog";
    public const string PubMed = "pubmed";
    public const string Uploaded = "uploaded";

    /// <summary>Every label the library can hold; used when a plan does not restrict the search.</summary>
    public static readonly string[] All = { DrugLabel, Guideline, EgyptCatalog, PubMed, Uploaded };

    /// <summary>
    /// Maps a library file (its path relative to the library folder) to a source type. The knowledge base
    /// ships egypt_drugs.jsonl, drugs.jsonl, guidelines.jsonl and pubmed/*.jsonl; anything else — a PDF or
    /// text file — is something the clinic uploaded itself.
    /// </summary>
    public static string FromFileName(string relativeName)
    {
        var name = relativeName.Replace('\\', '/').ToLowerInvariant();
        var file = name[(name.LastIndexOf('/') + 1)..];

        if (file.EndsWith(".jsonl", StringComparison.Ordinal))
        {
            if (file.Contains("egypt", StringComparison.Ordinal)) return EgyptCatalog;
            if (file.Contains("pubmed", StringComparison.Ordinal)) return PubMed;
            if (file.Contains("guideline", StringComparison.Ordinal) || file.Contains("statpearls", StringComparison.Ordinal))
                return Guideline;
            if (file.Contains("drug", StringComparison.Ordinal) || file.Contains("label", StringComparison.Ordinal))
                return DrugLabel;
            return Guideline;
        }

        return Uploaded;
    }
}
