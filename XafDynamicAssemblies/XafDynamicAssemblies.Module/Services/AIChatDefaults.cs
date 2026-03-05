using System.Collections.Generic;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Ganss.Xss;

namespace XafDynamicAssemblies.Module.Services
{
    /// <summary>
    /// Single source of truth for AI chat UI configuration shared
    /// across platform projects.
    /// </summary>
    public static class AIChatDefaults
    {
        // -- Header / Empty State -------------------------------------------

        public const string HeaderText = "Schema AI Assistant";

        public const string EmptyStateText =
            "Ask me anything about your schema — create entities, add fields, manage relationships & more.\nPowered by LLMTornado.";

        // -- Prompt Suggestions ---------------------------------------------

        /// <summary>
        /// Lightweight DTO used to build suggestion controls.
        /// </summary>
        public record PromptSuggestionItem(string Title, string Text, string Prompt);

        public static IReadOnlyList<PromptSuggestionItem> PromptSuggestions { get; } = new List<PromptSuggestionItem>
        {
            new("Create Entity",
                "Create a new runtime entity",
                "Create a new entity called Employee with fields: FirstName (string), LastName (string), Email (string), HireDate (DateTime), Salary (decimal)"),

            new("List Entities",
                "Show all runtime entities and their fields",
                "List all runtime entities with their fields and current status"),

            new("Add Fields",
                "Add fields to an existing entity",
                "Show me pending changes and help me add new fields to an entity"),

            new("Set Up Permissions",
                "Configure role-based access",
                "Help me set up role-based permissions for my runtime entities"),
        };

        // -- Markdown to HTML -----------------------------------------------

        private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
            .UsePipeTables()
            .UseEmphasisExtras()
            .UseAutoLinks()
            .UseTaskLists()
            .Build();

        private static readonly HtmlSanitizer Sanitizer = CreateSanitizer();

        private static HtmlSanitizer CreateSanitizer()
        {
            var sanitizer = new HtmlSanitizer();
            // Ensure table tags survive sanitization
            foreach (var tag in new[] { "table", "thead", "tbody", "tr", "th", "td" })
                sanitizer.AllowedTags.Add(tag);
            return sanitizer;
        }

        /// <summary>
        /// Converts a Markdown string to sanitized HTML.
        /// Thread-safe — the pipeline and sanitizer instances are reentrant.
        /// </summary>
        public static string ConvertMarkdownToHtml(string markdown)
        {
            if (string.IsNullOrEmpty(markdown))
                return string.Empty;

            var html = Markdown.ToHtml(markdown, Pipeline);
            return Sanitizer.Sanitize(html);
        }
    }
}
