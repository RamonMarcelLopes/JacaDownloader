@echo off
setlocal
cd /d "%~dp0"

echo [1/3] Building the interface (Next.js static export)...
pushd ui
call pnpm install || exit /b 1
call pnpm build || exit /b 1
popd

echo [2/3] Publishing the executable...
dotnet publish -c Release -o dist || exit /b 1

echo [3/3] Done: %~dp0dist\JacaDownloader.exe
