PROJECT_DIR=Jellyfin.Plugin.IgnoreEmptyFolders
TEST_PROJECT=tests/Jellyfin.Plugin.IgnoreEmptyFolders.Tests/Jellyfin.Plugin.IgnoreEmptyFolders.Tests.csproj
BUILD_CONFIG=Release

.PHONY: all restore build test lint clean publish

all: build

restore:
	dotnet restore $(PROJECT_DIR)
	dotnet restore $(TEST_PROJECT)

build: restore
	dotnet build $(PROJECT_DIR) -c $(BUILD_CONFIG)

test: restore
	dotnet test tests/Jellyfin.Plugin.IgnoreEmptyFolders.Tests/Jellyfin.Plugin.IgnoreEmptyFolders.Tests.csproj -c $(BUILD_CONFIG)

lint: restore
	@scripts/lint

clean:
	dotnet clean $(PROJECT_DIR)
	rm -rf $(PROJECT_DIR)/bin $(PROJECT_DIR)/obj

publish: restore
	dotnet publish $(PROJECT_DIR) -c $(BUILD_CONFIG) -o $(PUBLISH_DIR)
