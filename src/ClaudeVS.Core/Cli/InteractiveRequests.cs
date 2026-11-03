using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ClaudeVS.Core.Cli
{
    public sealed class QuestionOption
    {
        public string Label { get; set; }
        public string Description { get; set; }
        /// <summary>Optional mockup or code snippet to show while this option is highlighted (2.1.317).</summary>
        public string Preview { get; set; }
    }

    public sealed class Question
    {
        public string Text { get; set; }
        public string Header { get; set; }
        public bool MultiSelect { get; set; }
        public List<QuestionOption> Options { get; } = new List<QuestionOption>();
    }

    /// <summary>
    /// The AskUserQuestion tool, as the CLI sends it in a can_use_tool request:
    /// <c>input.questions[] = { question, header, options[{label, description}], multiSelect }</c>.
    /// It is answered by allowing the call with <c>updatedInput = input + answers{ questionText: label }</c>
    /// (verified with Claude Code 2.1.316: Claude received the chosen label).
    /// </summary>
    public static class AskUserQuestionTool
    {
        public const string Name = "AskUserQuestion";

        public static List<Question> Parse(JObject input)
        {
            var list = new List<Question>();
            if (!(input?["questions"] is JArray qs)) return list;
            foreach (var q in qs.OfType<JObject>())
            {
                var question = new Question
                {
                    Text = (string)q["question"] ?? "",
                    Header = (string)q["header"],
                    MultiSelect = q.Value<bool?>("multiSelect") ?? false,
                };
                if (q["options"] is JArray opts)
                    question.Options.AddRange(opts.OfType<JObject>().Select(o => new QuestionOption { Label = (string)o["label"] ?? "", Description = (string)o["description"], Preview = (string)o["preview"] }));
                list.Add(question);
            }
            return list;
        }

        /// <summary>
        /// The updatedInput for an "allow" response: the original input plus an answers map. Several choices for a
        /// multi-select question are joined with ", ". Unanswered questions are left out.
        /// </summary>
        public static JObject BuildAnswer(JObject input, IDictionary<string, IReadOnlyList<string>> chosen)
        {
            var result = (JObject)(input?.DeepClone() ?? new JObject());
            var answers = new JObject();
            var annotations = new JObject();
            var questions = Parse(input);
            foreach (var kv in chosen ?? new Dictionary<string, IReadOnlyList<string>>())
            {
                var labels = (kv.Value ?? new string[0]).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()).ToList();
                if (labels.Count == 0) continue;
                answers[kv.Key] = string.Join(", ", labels);
                // Like the CLI's own dialog: tell Claude which preview was picked, when the question had previews.
                var picked = questions.FirstOrDefault(q => q.Text == kv.Key)?.Options.Where(o => labels.Contains(o.Label) && !string.IsNullOrEmpty(o.Preview)).ToList();
                if (picked != null && picked.Count == 1 && labels.Count == 1) annotations[kv.Key] = new JObject { ["preview"] = picked[0].Preview };
            }
            result["answers"] = answers;
            if (annotations.Count > 0) result["annotations"] = annotations;
            return result;
        }
    }

    /// <summary>
    /// The ExitPlanMode tool: <c>input = { plan (Markdown), planFilePath }</c>. Allowing it approves the plan and
    /// the CLI leaves plan mode and starts the work; denying it with a message sends that feedback back to Claude.
    /// </summary>
    public static class ExitPlanModeTool
    {
        public const string Name = "ExitPlanMode";

        public static string Plan(JObject input) => (string)input?["plan"] ?? "";

        public static string RevisionMessage(string feedback) =>
            string.IsNullOrWhiteSpace(feedback)
                ? "The user did not approve the plan. Ask what they would like changed."
                : "The user did not approve the plan yet. Revise it based on this feedback and present it again: " + feedback.Trim();
    }
}
