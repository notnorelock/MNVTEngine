@echo off
setlocal
rem Pipeline build of the engine (docs/architecture/versioning.md): allocates one version, MAJOR.DDMM.PATCH.BUILD with
rem the next build number, stamps every project of the solution with it, and records the result in artifacts/build-state.
rem   scripts\build.cmd [Debug|Release] [more dotnet build arguments]
rem Plain `dotnet build` is a development build instead: BUILD 0, nothing allocated.
set MSBUILDDISABLENODEREUSE=1
set "ROOT=%~dp0.."
set "CONFIGURATION=%~1"
if /i "%CONFIGURATION%"=="Debug" (shift) else if /i "%CONFIGURATION%"=="Release" (shift) else (set "CONFIGURATION=Debug")
set "ARGS="
:args
if "%~1"=="" goto run
set ARGS=%ARGS% %1
shift
goto args
:run
set "SESSION=%ROOT%\artifacts\version\sessions\%RANDOM%-%RANDOM%-%RANDOM%"
dotnet msbuild "%ROOT%\eng\Version.proj" -t:BuildGenerator -nologo -v:q || exit /b 1
dotnet "%ROOT%\artifacts\versioning-tool\MNVTEngine.Versioning.dll" resolve --allocate --root "%ROOT%" --out "%SESSION%" --configuration %CONFIGURATION% || exit /b 1
dotnet build "%ROOT%\MNVTEngine.sln" -c %CONFIGURATION% "-p:MnvtVersionSession=%SESSION%" %ARGS%
set RESULT=%ERRORLEVEL%
if %RESULT%==0 (set "OUTCOME=succeeded") else (set "OUTCOME=failed")
dotnet "%ROOT%\artifacts\versioning-tool\MNVTEngine.Versioning.dll" complete --root "%ROOT%" --session "%SESSION%" --result %OUTCOME%
exit /b %RESULT%
