using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Vein.Compiler.Lsp;
using Xunit;

namespace Vein.Tests;

// The VS Code extension (editors/vscode) against what it must agree with. Drift there fails nothing at
// build time: a keyword the grammar omits renders as plain text, a trigger the manifest does not list never
// opens a list. So the grammar's keywords are checked against the lexer in both directions — as
// SyntaxThemeTests checks the Workbench theme — and the manifest against the server.
public class EditorsTests
{
    private static string Ext(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "stdlib"))) dir = dir.Parent;
        return Path.Combine(new[] { dir!.FullName, "editors", "vscode" }.Concat(parts).ToArray());
    }

    private static IReadOnlySet<string> GrammarKeywords()
    {
        var grammar = JsonNode.Parse(File.ReadAllText(Ext("syntaxes", "veinscript.tmLanguage.json")))!;
        string match = grammar["repository"]!["keywords"]!["match"]!.GetValue<string>();
        var inner = Regex.Match(match, @"\\b\((.*)\)\\b").Groups[1].Value;
        return inner.Split('|').ToHashSet(StringComparer.Ordinal);
    }

    [Fact]
    public void The_grammar_paints_every_lexer_keyword_and_nothing_else()
    {
        var grammar = GrammarKeywords();
        var lexer = Vein.Compiler.Lexing.Lexer.KeywordNames.ToHashSet(StringComparer.Ordinal);
        Assert.Empty(lexer.Except(grammar));
        Assert.Empty(grammar.Except(lexer));
    }

    [Fact]
    public void The_manifest_claims_vein_files_and_is_not_the_old_grammar_only_language()
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Ext("package.json")))!;
        var language = manifest["contributes"]!["languages"]![0]!;
        Assert.Equal("veinscript", language["id"]!.GetValue<string>());
        Assert.Contains(".vein", language["extensions"]!.AsArray().Select(e => e!.GetValue<string>()));
        Assert.Equal("source.veinscript", manifest["contributes"]!["grammars"]![0]!["scopeName"]!.GetValue<string>());
    }

    [Fact]
    public void The_semantic_colours_name_the_token_types_the_server_sends()
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Ext("package.json")))!;
        var rules = manifest["contributes"]!["configurationDefaults"]!["editor.semanticTokenColorCustomizations"]!["rules"]!.AsObject();
        foreach (var type in LspServer.TokenTypes)
            Assert.Contains(type + ":veinscript", rules.Select(r => r.Key));
    }
}
