@echo off
rem veinc — convenience wrapper for the VeinScript CLI. Forwards to Vein.Cli.
rem   veinc run samples\console.vein   /   veinc build samples\console.vein   /   veinc symbols app.vein
dotnet run --project "%~dp0src\Vein.Cli" --verbosity quiet -- %*
