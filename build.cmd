@echo off
rem 重新发布:输出单个自包含 exe 到 publish\ 目录
dotnet publish -c Release -o publish -p:IncludeNativeLibrariesForSelfExtract=true
