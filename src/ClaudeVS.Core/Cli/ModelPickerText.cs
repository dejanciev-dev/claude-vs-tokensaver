using System.Globalization;
using ClaudeVS.Core.Tokens;

namespace ClaudeVS.Core.Cli
{
    /// <summary>What the model picker says for each choice: Claude Code's name for it and what it costs.</summary>
    public static class ModelPickerText
    {
        /// <summary>"Haiku 5.5" when Claude Code's models list names what the choice resolves to, else "Haiku"; Auto's own label.</summary>
        public static string Label(ModelChoice m, ModelCatalog models)
        {
            if (m == ModelChoice.Auto) return "Auto (route each prompt)";
            string id = models?.ResolvedId(m.ToCli());
            return (id != null ? models.DisplayName(id) : null) ?? m.ToString();
        }

        /// <summary>
        /// "$0.10 / $0.50 per Mtok in / out" from Options → Pricing, and for a model dearer than Opus "; 2× the Opus price;
        /// Auto never picks it". No price when the choice resolves to a model Options doesn't price and none was learned
        /// (<c>ANTHROPIC_DEFAULT_HAIKU_MODEL</c> pointing at an older one). Null for Auto.
        /// </summary>
        public static string Description(ModelChoice m, ModelCatalog models, PriceTable prices)
        {
            if (m == ModelChoice.Auto) return null;
            prices = prices ?? new PriceTable();
            string id = models?.ResolvedId(m.ToCli());
            ModelPrice p = id == null || PriceTable.IsCurrent(id) ? prices.For(m.ToCli()) : PriceTable.LearnedPrice(id);
            string price = p == null ? null : $"{Usd(p.Input)} / {Usd(p.Output)} per Mtok in / out";
            string dearer = prices.VersusOpus(m.ToCli()) is string v ? v + "; Auto never picks it" : null;
            return price != null && dearer != null ? price + "; " + dearer : price ?? dearer;
        }

        /// <summary>"$0.10", "$4", "$2.50".</summary>
        public static string Usd(double v)
        {
            var c = CultureInfo.InvariantCulture;
            if (v < 1) return "$" + v.ToString("0.00##", c);
            return "$" + (System.Math.Abs(v - System.Math.Round(v)) < 1e-9 ? v.ToString("0", c) : v.ToString("0.00", c));
        }
    }
}
