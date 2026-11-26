using System.Collections.Generic;

namespace ClaudeVS.Core.Markdown
{
    /// <summary>
    /// What a code block saved as a file (Phase 100) is called by default: a name and file type from the fence's
    /// language (<c>csharp</c> → <c>snippet.cs</c>, <c>dockerfile</c> → <c>Dockerfile</c>), and the save dialog's
    /// filter for it.
    /// </summary>
    public sealed class CodeFileName
    {
        /// <summary>The name the dialog suggests: "snippet.cs", "Dockerfile".</summary>
        public string FileName { get; private set; }
        /// <summary>".cs", or "" for a name without one (Dockerfile, Makefile).</summary>
        public string Extension { get; private set; }
        /// <summary>"C# (*.cs)|*.cs|All files (*.*)|*.*".</summary>
        public string Filter { get; private set; }

        private static readonly Dictionary<string, (string Ext, string Kind)> Known = new Dictionary<string, (string, string)>
        {
            ["cs"] = (".cs", "C#"), ["c#"] = (".cs", "C#"), ["csharp"] = (".cs", "C#"),
            ["razor"] = (".razor", "Razor"), ["cshtml"] = (".cshtml", "Razor"),
            ["js"] = (".js", "JavaScript"), ["javascript"] = (".js", "JavaScript"), ["jsx"] = (".jsx", "JavaScript"), ["mjs"] = (".mjs", "JavaScript"),
            ["ts"] = (".ts", "TypeScript"), ["typescript"] = (".ts", "TypeScript"), ["tsx"] = (".tsx", "TypeScript"),
            ["py"] = (".py", "Python"), ["python"] = (".py", "Python"),
            ["ps"] = (".ps1", "PowerShell"), ["ps1"] = (".ps1", "PowerShell"), ["powershell"] = (".ps1", "PowerShell"), ["pwsh"] = (".ps1", "PowerShell"),
            ["sh"] = (".sh", "Shell script"), ["bash"] = (".sh", "Shell script"), ["shell"] = (".sh", "Shell script"), ["zsh"] = (".sh", "Shell script"),
            ["bat"] = (".cmd", "Batch file"), ["cmd"] = (".cmd", "Batch file"), ["batch"] = (".cmd", "Batch file"),
            ["sql"] = (".sql", "SQL"), ["tsql"] = (".sql", "SQL"), ["mysql"] = (".sql", "SQL"), ["postgres"] = (".sql", "SQL"), ["postgresql"] = (".sql", "SQL"),
            ["json"] = (".json", "JSON"), ["jsonc"] = (".json", "JSON"), ["json5"] = (".json5", "JSON"),
            ["yaml"] = (".yml", "YAML"), ["yml"] = (".yml", "YAML"),
            ["xml"] = (".xml", "XML"), ["xaml"] = (".xaml", "XAML"), ["csproj"] = (".csproj", "Project file"), ["props"] = (".props", "MSBuild file"), ["targets"] = (".targets", "MSBuild file"),
            ["html"] = (".html", "HTML"), ["htm"] = (".html", "HTML"), ["svg"] = (".svg", "SVG"), ["vue"] = (".vue", "Vue"),
            ["css"] = (".css", "CSS"), ["scss"] = (".scss", "SCSS"), ["less"] = (".less", "Less"),
            ["c"] = (".c", "C"), ["h"] = (".h", "C header"), ["cpp"] = (".cpp", "C++"), ["c++"] = (".cpp", "C++"), ["cc"] = (".cpp", "C++"), ["hpp"] = (".hpp", "C++ header"),
            ["go"] = (".go", "Go"), ["golang"] = (".go", "Go"), ["rust"] = (".rs", "Rust"), ["rs"] = (".rs", "Rust"),
            ["java"] = (".java", "Java"), ["kotlin"] = (".kt", "Kotlin"), ["kt"] = (".kt", "Kotlin"), ["scala"] = (".scala", "Scala"),
            ["rb"] = (".rb", "Ruby"), ["ruby"] = (".rb", "Ruby"), ["php"] = (".php", "PHP"), ["swift"] = (".swift", "Swift"),
            ["dart"] = (".dart", "Dart"), ["lua"] = (".lua", "Lua"), ["r"] = (".R", "R"),
            ["toml"] = (".toml", "TOML"), ["ini"] = (".ini", "INI"), ["cfg"] = (".cfg", "Settings"), ["conf"] = (".conf", "Settings"), ["properties"] = (".properties", "Settings"),
            ["graphql"] = (".graphql", "GraphQL"), ["gql"] = (".graphql", "GraphQL"),
            ["hcl"] = (".hcl", "HCL"), ["tf"] = (".tf", "Terraform"), ["terraform"] = (".tf", "Terraform"),
            ["diff"] = (".diff", "Diff"), ["patch"] = (".patch", "Patch"), ["udiff"] = (".diff", "Diff"),
            ["md"] = (".md", "Markdown"), ["markdown"] = (".md", "Markdown"),
        };

        private static readonly Dictionary<string, string> WholeNames = new Dictionary<string, string>
        {
            ["dockerfile"] = "Dockerfile", ["docker"] = "Dockerfile", ["containerfile"] = "Containerfile",
            ["makefile"] = "Makefile", ["make"] = "Makefile", ["gemfile"] = "Gemfile",
            ["editorconfig"] = ".editorconfig", ["gitconfig"] = ".gitconfig",
        };

        public static CodeFileName For(string language)
        {
            string lang = (language ?? "").Trim().ToLowerInvariant();
            if (WholeNames.TryGetValue(lang, out string whole))
                return new CodeFileName { FileName = whole, Extension = "", Filter = whole + "|" + whole + "|All files (*.*)|*.*" };
            if (!Known.TryGetValue(lang, out var known)) known = (".txt", "Text");
            return new CodeFileName
            {
                FileName = "snippet" + known.Ext,
                Extension = known.Ext,
                Filter = $"{known.Kind} (*{known.Ext})|*{known.Ext}|All files (*.*)|*.*",
            };
        }
    }
}
