PROJECT_NAME=Jellyfin.Plugin.IgnoreEmptyFolders
PROJECT_DIR=$(PROJECT_NAME)
BUILD_CONFIG=Release

.PHONY: all build clean restore publish

all: build

restore:
	dotnet restore $(PROJECT_DIR)

build: restore
	dotnet build $(PROJECT_DIR) -c $(BUILD_CONFIG)

clean:
	dotnet clean $(PROJECT_DIR)
	rm -rf $(PROJECT_DIR)/bin $(PROJECT_DIR)/obj

publish: build
	dotnet publish $(PROJECT_DIR) -c $(BUILD_CONFIG) -o ./publish
