.PHONY: dev build

dev:
	dotnet run --project app -p:WarningLevel=0

build:
	dotnet build app -p:WarningLevel=0
