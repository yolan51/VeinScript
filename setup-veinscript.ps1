# VeinScript compiler — project bootstrap
#
# Put this in the folder that already contains:
#   demo.vein, TokenKind(1).cs (or TokenKind.cs), Lexer.cs, README.md
# then run:
#   .\setup-veinscript.ps1
#
# It creates the directory layout, writes the files that were missing from the
# download, and moves your existing files into place.

$ErrorActionPreference = 'Stop'

$root = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
Write-Host "Setting up VeinScript project in: $root" -ForegroundColor Cyan

# --- directories -------------------------------------------------------------

$dirs = @(
    'src\Vein.Compiler\Lexing',
    'src\Vein.Compiler\Diagnostics',
    'src\Vein.Compiler\Parsing',
    'src\Vein.Cli',
    'samples',
    'tests\golden'
)
foreach ($d in $dirs) {
    $full = Join-Path $root $d
    if (-not (Test-Path $full)) { New-Item -ItemType Directory -Path $full -Force | Out-Null }
}
Write-Host "  directories created" -ForegroundColor Green

# --- move downloaded files into place ----------------------------------------

function Move-IfFound($pattern, $destination) {
    $found = Get-ChildItem -Path $root -Filter $pattern -File -ErrorAction SilentlyContinue |
             Select-Object -First 1
    if ($found) {
        $dest = Join-Path $root $destination
        Move-Item -Path $found.FullName -Destination $dest -Force
        Write-Host "  moved $($found.Name) -> $destination" -ForegroundColor Green
    } else {
        Write-Host "  NOT FOUND: $pattern (expected at $destination)" -ForegroundColor Yellow
    }
}

Move-IfFound 'TokenKind*.cs' 'src\Vein.Compiler\Lexing\TokenKind.cs'
Move-IfFound 'Lexer*.cs'     'src\Vein.Compiler\Lexing\Lexer.cs'
Move-IfFound 'demo*.vein'    'samples\demo.vein'

# --- files that were missing from the download -------------------------------

# NOTE: all here-strings are single-quoted (@' ... '@) so PowerShell does not try
# to expand $ in the C# source. This matters a lot here -- the lexer is full of them.

$tokenCs = @'
namespace Vein.Compiler.Lexing;

public readonly record struct SourceSpan(string File, int Line, int Col, int Length)
{
    public override string ToString() => $"{File}:{Line}:{Col}";
}

public readonly record struct Token(
    TokenKind Kind,
    string Text,        // sigils stripped; for Ident this is the raw name
    object? Value,      // long / double / string for literals, else null
    SourceSpan Span)
{
    public override string ToString() =>
        Value is null ? $"{Kind}({Text})" : $"{Kind}({Text}={Value})";
}
'@

$diagnosticCs = @'
using Vein.Compiler.Lexing;

namespace Vein.Compiler.Diagnostics;

public enum Severity { Error, Warning, Info }

public sealed record Diagnostic(Severity Severity, string Code, string Message, SourceSpan Span)
{
    public override string ToString() =>
        $"{Span}: {Severity.ToString().ToLowerInvariant()} {Code}: {Message}";
}

public sealed class DiagnosticBag
{
    private readonly List<Diagnostic> _items = new();

    public IReadOnlyList<Diagnostic> Items => _items;
    public bool HasErrors => _items.Any(d => d.Severity == Severity.Error);

    public void Error(string code, string message, SourceSpan span) =>
        _items.Add(new Diagnostic(Severity.Error, code, message, span));

    public void Warning(string code, string message, SourceSpan span) =>
        _items.Add(new Diagnostic(Severity.Warning, code, message, span));
}
'@

$compilerProj = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <RootNamespace>Vein.Compiler</RootNamespace>
  </PropertyGroup>
</Project>
'@

$cliProj = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <AssemblyName>veinc</AssemblyName>
    <RootNamespace>Vein.Cli</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Vein.Compiler\Vein.Compiler.csproj" />
  </ItemGroup>
</Project>
'@

$programCs = @'
using Vein.Compiler.Diagnostics;
using Vein.Compiler.Lexing;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: veinc tokens <file.vein>");
    return 2;
}

string command = args[0];
string path = args[1];

if (!File.Exists(path))
{
    Console.Error.WriteLine($"file not found: {path}");
    return 2;
}

string source = File.ReadAllText(path);
var diagnostics = new DiagnosticBag();

switch (command)
{
    case "tokens":
    {
        var tokens = new Lexer(source, Path.GetFileName(path), diagnostics).Tokenize();
        foreach (var t in tokens)
            Console.WriteLine($"{t.Span,-24} {t.Kind,-14} {Display(t)}");
        break;
    }

    default:
        Console.Error.WriteLine($"unknown command: {command}");
        return 2;
}

foreach (var d in diagnostics.Items) Console.Error.WriteLine(d);
return diagnostics.HasErrors ? 1 : 0;

static string Display(Token t) =>
    t.Kind == TokenKind.Term ? "" :
    t.Value is not null ? $"{t.Text}  = {t.Value}" : t.Text;
'@

$gitignore = @'
bin/
obj/
*.user
'@

function Write-File($relative, $content) {
    $full = Join-Path $root $relative
    # UTF8 without BOM -- Roslyn is fine either way, but keeps diffs clean
    [System.IO.File]::WriteAllText($full, $content, (New-Object System.Text.UTF8Encoding $false))
    Write-Host "  wrote $relative" -ForegroundColor Green
}

Write-File 'src\Vein.Compiler\Lexing\Token.cs'            $tokenCs
Write-File 'src\Vein.Compiler\Diagnostics\Diagnostic.cs'  $diagnosticCs
Write-File 'src\Vein.Compiler\Vein.Compiler.csproj'       $compilerProj
Write-File 'src\Vein.Cli\Vein.Cli.csproj'                 $cliProj
Write-File 'src\Vein.Cli\Program.cs'                      $programCs
Write-File '.gitignore'                                   $gitignore

# --- solution ----------------------------------------------------------------

Push-Location $root
try {
    if (-not (Test-Path (Join-Path $root 'VeinScript.sln'))) {
        dotnet new sln -n VeinScript | Out-Null
        dotnet sln add src\Vein.Compiler\Vein.Compiler.csproj | Out-Null
        dotnet sln add src\Vein.Cli\Vein.Cli.csproj | Out-Null
        Write-Host "  created VeinScript.sln" -ForegroundColor Green
    }
} catch {
    Write-Host "  skipped solution (dotnet not on PATH?)" -ForegroundColor Yellow
} finally {
    Pop-Location
}

# --- done --------------------------------------------------------------------

Write-Host ""
Write-Host "Done. Now run:" -ForegroundColor Cyan
Write-Host "  dotnet run --project src\Vein.Cli -- tokens samples\demo.vein" -ForegroundColor White
Write-Host ""
Write-Host "Expected: a token dump ending in EndOfFile, no errors." -ForegroundColor Gray
