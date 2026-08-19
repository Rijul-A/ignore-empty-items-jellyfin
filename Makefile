PROJECT_DIR=Jellyfin.Plugin.IgnoreEmptyFolders
BUILD_CONFIG=Release
PUBLISH_DIR=publish

.PHONY: all restore build lint clean publish build-check install-hooks

all: build

restore:
	dotnet restore $(PROJECT_DIR)

build: restore
	dotnet build $(PROJECT_DIR) -c $(BUILD_CONFIG)

lint: restore
	@scripts/lint

# Fast build check for pre-commit hook (uses isolated obj folder and no-restore)
build-check:
	@dotnet build $(PROJECT_DIR) -c Release --no-restore -o ./obj/pre-commit-build > /dev/null 2>&1

clean:
	dotnet clean $(PROJECT_DIR)
	rm -rf $(PROJECT_DIR)/bin $(PROJECT_DIR)/obj ./obj/pre-commit-build

publish: restore
	dotnet publish $(PROJECT_DIR) -c $(BUILD_CONFIG) -o $(PUBLISH_DIR)

install-hooks:
	@echo "Installing git hooks..."
	@ln -sf ../../scripts/pre-commit .git/hooks/pre-commit
	@chmod +x scripts/pre-commit
	@echo "Git hooks installed successfully."
