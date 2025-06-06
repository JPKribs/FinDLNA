# FinDLNA Build Makefile
# Builds self-contained, ready-to-deploy binaries for multiple platforms

PROJECT_NAME = FinDLNA
VERSION ?= 1.0.0
BUILD_DIR = build
PUBLISH_DIR = publish
DIST_DIR = dist

# .NET Configuration
DOTNET_VERSION = net9.0
CONFIGURATION = Release

# Platform targets
PLATFORMS = linux-arm64 linux-x64 win-x64 osx-x64 osx-arm64

# Default target
.PHONY: all
all: clean build-all package-all

# MARK: Clean build directories
.PHONY: clean
clean:
	@echo "🧹 Cleaning build directories..."
	rm -rf $(BUILD_DIR)
	rm -rf $(PUBLISH_DIR)
	rm -rf $(DIST_DIR)
	dotnet clean

# MARK: Restore dependencies
.PHONY: restore
restore:
	@echo "📦 Restoring dependencies..."
	dotnet restore

# MARK: Build all platforms
.PHONY: build-all
build-all: restore
	@echo "🔨 Building for all platforms..."
	@for platform in $(PLATFORMS); do \
		$(MAKE) build-platform PLATFORM=$$platform; \
	done

# MARK: Build specific platform
.PHONY: build-platform
build-platform:
	@echo "🔨 Building $(PROJECT_NAME) for $(PLATFORM)..."
	@mkdir -p $(PUBLISH_DIR)/$(PLATFORM)
	dotnet publish $(PROJECT_NAME).csproj \
		--configuration $(CONFIGURATION) \
		--runtime $(PLATFORM) \
		--self-contained true \
		--output $(PUBLISH_DIR)/$(PLATFORM) \
		/p:PublishSingleFile=true \
		/p:IncludeNativeLibrariesForSelfExtract=true \
		/p:PublishTrimmed=false \
		/p:Version=$(VERSION)

# MARK: Package all builds
.PHONY: package-all
package-all:
	@echo "📦 Packaging all builds..."
	@mkdir -p $(DIST_DIR)
	@for platform in $(PLATFORMS); do \
		$(MAKE) package-platform PLATFORM=$$platform; \
	done

# MARK: Package specific platform
.PHONY: package-platform
package-platform:
	@echo "📦 Packaging $(PROJECT_NAME) for $(PLATFORM)..."
	@cd $(PUBLISH_DIR) && \
	if [ "$(PLATFORM)" = "win-x64" ]; then \
		zip -r ../$(DIST_DIR)/$(PROJECT_NAME)-$(VERSION)-$(PLATFORM).zip $(PLATFORM)/; \
	else \
		tar -czf ../$(DIST_DIR)/$(PROJECT_NAME)-$(VERSION)-$(PLATFORM).tar.gz $(PLATFORM)/; \
	fi

# MARK: Individual platform targets
.PHONY: linux-arm64
linux-arm64:
	@$(MAKE) build-platform PLATFORM=linux-arm64
	@$(MAKE) package-platform PLATFORM=linux-arm64

.PHONY: linux-x64
linux-x64:
	@$(MAKE) build-platform PLATFORM=linux-x64
	@$(MAKE) package-platform PLATFORM=linux-x64

.PHONY: win-x64
win-x64:
	@$(MAKE) build-platform PLATFORM=win-x64
	@$(MAKE) package-platform PLATFORM=win-x64

.PHONY: osx-x64
osx-x64:
	@$(MAKE) build-platform PLATFORM=osx-x64
	@$(MAKE) package-platform PLATFORM=osx-x64

.PHONY: osx-arm64
osx-arm64:
	@$(MAKE) build-platform PLATFORM=osx-arm64
	@$(MAKE) package-platform PLATFORM=osx-arm64

# MARK: Linux ARM specific target (alias)
.PHONY: linux-arm64
linux-arm64:
	@$(MAKE) build-platform PLATFORM=linux-arm64
	@$(MAKE) package-platform PLATFORM=linux-arm64
	@echo "🍓 Linux ARM64 build completed!"
	@echo "   Extract: $(DIST_DIR)/$(PROJECT_NAME)-$(VERSION)-linux-arm64.tar.gz"
	@echo "   Run: ./$(PROJECT_NAME)"

# MARK: Raspberry Pi alias
.PHONY: raspberry-pi
raspberry-pi: linux-arm64
	@echo "🍓 Raspberry Pi build completed!"

# MARK: Development build (local platform)
.PHONY: dev
dev: restore
	@echo "🔧 Development build..."
	dotnet build --configuration Debug

# MARK: Run development server
.PHONY: run
run: dev
	@echo "🚀 Starting development server..."
	dotnet run

# MARK: Test
.PHONY: test
test: restore
	@echo "🧪 Running tests..."
	dotnet test

# MARK: Docker build
.PHONY: docker
docker:
	@echo "🐳 Building Docker image..."
	docker build -t $(PROJECT_NAME):$(VERSION) -t $(PROJECT_NAME):latest .

# MARK: Install systemd service (Linux only)
.PHONY: install-service
install-service:
	@echo "⚙️  Installing systemd service..."
	@if [ ! -f $(DIST_DIR)/$(PROJECT_NAME)-$(VERSION)-linux-x64.tar.gz ] && [ ! -f $(DIST_DIR)/$(PROJECT_NAME)-$(VERSION)-linux-arm64.tar.gz ]; then \
		echo "❌ No Linux build found. Run 'make linux-x64' or 'make linux-arm64' first."; \
		exit 1; \
	fi
	@sudo mkdir -p /opt/$(PROJECT_NAME)
	@if [ -f $(DIST_DIR)/$(PROJECT_NAME)-$(VERSION)-linux-x64.tar.gz ]; then \
		sudo tar -xzf $(DIST_DIR)/$(PROJECT_NAME)-$(VERSION)-linux-x64.tar.gz -C /opt/$(PROJECT_NAME) --strip-components=1; \
	else \
		sudo tar -xzf $(DIST_DIR)/$(PROJECT_NAME)-$(VERSION)-linux-arm64.tar.gz -C /opt/$(PROJECT_NAME) --strip-components=1; \
	fi
	@sudo chmod +x /opt/$(PROJECT_NAME)/$(PROJECT_NAME)
	@echo "[Unit]" | sudo tee /etc/systemd/system/$(PROJECT_NAME).service
	@echo "Description=$(PROJECT_NAME) DLNA Proxy Server" | sudo tee -a /etc/systemd/system/$(PROJECT_NAME).service
	@echo "After=network.target" | sudo tee -a /etc/systemd/system/$(PROJECT_NAME).service
	@echo "" | sudo tee -a /etc/systemd/system/$(PROJECT_NAME).service
	@echo "[Service]" | sudo tee -a /etc/systemd/system/$(PROJECT_NAME).service
	@echo "Type=notify" | sudo tee -a /etc/systemd/system/$(PROJECT_NAME).service
	@echo "ExecStart=/opt/$(PROJECT_NAME)/$(PROJECT_NAME)" | sudo tee -a /etc/systemd/system/$(PROJECT_NAME).service
	@echo "WorkingDirectory=/opt/$(PROJECT_NAME)" | sudo tee -a /etc/systemd/system/$(PROJECT_NAME).service
	@echo "User=findlna" | sudo tee -a /etc/systemd/system/$(PROJECT_NAME).service
	@echo "Group=findlna" | sudo tee -a /etc/systemd/system/$(PROJECT_NAME).service
	@echo "Restart=always" | sudo tee -a /etc/systemd/system/$(PROJECT_NAME).service
	@echo "RestartSec=10" | sudo tee -a /etc/systemd/system/$(PROJECT_NAME).service
	@echo "Environment=ASPNETCORE_ENVIRONMENT=Production" | sudo tee -a /etc/systemd/system/$(PROJECT_NAME).service
	@echo "Environment=ASPNETCORE_URLS=http://0.0.0.0:5000" | sudo tee -a /etc/systemd/system/$(PROJECT_NAME).service
	@echo "" | sudo tee -a /etc/systemd/system/$(PROJECT_NAME).service
	@echo "[Install]" | sudo tee -a /etc/systemd/system/$(PROJECT_NAME).service
	@echo "WantedBy=multi-user.target" | sudo tee -a /etc/systemd/system/$(PROJECT_NAME).service
	@sudo useradd -r -s /bin/false findlna || true
	@sudo chown -R findlna:findlna /opt/$(PROJECT_NAME)
	@sudo systemctl daemon-reload
	@sudo systemctl enable $(PROJECT_NAME)
	@echo "✅ Service installed! Start with: sudo systemctl start $(PROJECT_NAME)"

# MARK: Uninstall systemd service
.PHONY: uninstall-service
uninstall-service:
	@echo "🗑️  Uninstalling systemd service..."
	@sudo systemctl stop $(PROJECT_NAME) || true
	@sudo systemctl disable $(PROJECT_NAME) || true
	@sudo rm -f /etc/systemd/system/$(PROJECT_NAME).service
	@sudo rm -rf /opt/$(PROJECT_NAME)
	@sudo systemctl daemon-reload
	@echo "✅ Service uninstalled!"

# MARK: Create Windows installer
.PHONY: windows-installer
windows-installer: win-x64
	@echo "🪟 Creating Windows installer..."
	@mkdir -p $(BUILD_DIR)/windows-installer
	@cp -r $(PUBLISH_DIR)/win-x64/* $(BUILD_DIR)/windows-installer/
	@echo "@echo off" > $(BUILD_DIR)/windows-installer/install.bat
	@echo "echo Installing FinDLNA..." >> $(BUILD_DIR)/windows-installer/install.bat
	@echo "mkdir \"C:\\Program Files\\FinDLNA\" 2>nul" >> $(BUILD_DIR)/windows-installer/install.bat
	@echo "copy /Y \"*.exe\" \"C:\\Program Files\\FinDLNA\\\"" >> $(BUILD_DIR)/windows-installer/install.bat
	@echo "copy /Y \"*.dll\" \"C:\\Program Files\\FinDLNA\\\" 2>nul" >> $(BUILD_DIR)/windows-installer/install.bat
	@echo "copy /Y \"*.json\" \"C:\\Program Files\\FinDLNA\\\" 2>nul" >> $(BUILD_DIR)/windows-installer/install.bat
	@echo "echo FinDLNA installed to C:\\Program Files\\FinDLNA\\" >> $(BUILD_DIR)/windows-installer/install.bat
	@echo "echo Run: \"C:\\Program Files\\FinDLNA\\FinDLNA.exe\"" >> $(BUILD_DIR)/windows-installer/install.bat
	@echo "pause" >> $(BUILD_DIR)/windows-installer/install.bat
	@cd $(BUILD_DIR) && zip -r ../$(DIST_DIR)/$(PROJECT_NAME)-$(VERSION)-windows-installer.zip windows-installer/

# MARK: Create macOS app bundle
.PHONY: macos-app
macos-app: osx-x64 osx-arm64
	@echo "🍎 Creating macOS app bundle..."
	@mkdir -p $(BUILD_DIR)/$(PROJECT_NAME).app/Contents/MacOS
	@mkdir -p $(BUILD_DIR)/$(PROJECT_NAME).app/Contents/Resources
	@echo "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" > $(BUILD_DIR)/$(PROJECT_NAME).app/Contents/Info.plist
	@echo "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">" >> $(BUILD_DIR)/$(PROJECT_NAME).app/Contents/Info.plist
	@echo "<plist version=\"1.0\">" >> $(BUILD_DIR)/$(PROJECT_NAME).app/Contents/Info.plist
	@echo "<dict>" >> $(BUILD_DIR)/$(PROJECT_NAME).app/Contents/Info.plist
	@echo "  <key>CFBundleDisplayName</key>" >> $(BUILD_DIR)/$(PROJECT_NAME).app/Contents/Info.plist
	@echo "  <string>FinDLNA</string>" >> $(BUILD_DIR)/$(PROJECT_NAME).app/Contents/Info.plist
	@echo "  <key>CFBundleExecutable</key>" >> $(BUILD_DIR)/$(PROJECT_NAME).app/Contents/Info.plist
	@echo "  <string>FinDLNA</string>" >> $(BUILD_DIR)/$(PROJECT_NAME).app/Contents/Info.plist
	@echo "  <key>CFBundleIdentifier</key>" >> $(BUILD_DIR)/$(PROJECT_NAME).app/Contents/Info.plist
	@echo "  <string>com.findlna.app</string>" >> $(BUILD_DIR)/$(PROJECT_NAME).app/Contents/Info.plist
	@echo "  <key>CFBundleVersion</key>" >> $(BUILD_DIR)/$(PROJECT_NAME).app/Contents/Info.plist
	@echo "  <string>$(VERSION)</string>" >> $(BUILD_DIR)/$(PROJECT_NAME).app/Contents/Info.plist
	@echo "</dict>" >> $(BUILD_DIR)/$(PROJECT_NAME).app/Contents/Info.plist
	@echo "</plist>" >> $(BUILD_DIR)/$(PROJECT_NAME).app/Contents/Info.plist
	@if [ -d $(PUBLISH_DIR)/osx-arm64 ]; then \
		cp -r $(PUBLISH_DIR)/osx-arm64/* $(BUILD_DIR)/$(PROJECT_NAME).app/Contents/MacOS/; \
	else \
		cp -r $(PUBLISH_DIR)/osx-x64/* $(BUILD_DIR)/$(PROJECT_NAME).app/Contents/MacOS/; \
	fi
	@chmod +x $(BUILD_DIR)/$(PROJECT_NAME).app/Contents/MacOS/$(PROJECT_NAME)
	@cd $(BUILD_DIR) && tar -czf ../$(DIST_DIR)/$(PROJECT_NAME)-$(VERSION)-macos.tar.gz $(PROJECT_NAME).app

# MARK: Show build info
.PHONY: info
info:
	@echo "📋 Build Information"
	@echo "   Project: $(PROJECT_NAME)"
	@echo "   Version: $(VERSION)"
	@echo "   .NET: $(DOTNET_VERSION)"
	@echo "   Config: $(CONFIGURATION)"
	@echo ""
	@echo "🎯 Available Targets:"
	@echo "   all           - Build all platforms and package"
	@echo "   linux-arm64   - Build for Linux ARM64"
	@echo "   linux-x64     - Build for Linux x64"
	@echo "   win-x64       - Build for Windows x64"
	@echo "   osx-x64       - Build for macOS Intel"
	@echo "   osx-arm64     - Build for macOS Apple Silicon"
	@echo "   dev           - Development build"
	@echo "   run           - Run development server"
	@echo "   test          - Run tests"
	@echo "   docker        - Build Docker image"
	@echo "   install-service - Install Linux systemd service"
	@echo "   windows-installer - Create Windows installer"
	@echo "   macos-app     - Create macOS app bundle"
	@echo ""
	@echo "📁 Output Directories:"
	@echo "   $(PUBLISH_DIR)/ - Platform binaries"
	@echo "   $(DIST_DIR)/    - Distribution packages"

# MARK: Check prerequisites
.PHONY: check
check:
	@echo "🔍 Checking prerequisites..."
	@command -v dotnet >/dev/null 2>&1 || (echo "❌ .NET not found. Install from https://dot.net" && exit 1)
	@dotnet --version | grep -E "^9\." >/dev/null || (echo "❌ .NET 9.0 required" && exit 1)
	@echo "✅ .NET 9.0 found"
	@command -v make >/dev/null 2>&1 || (echo "❌ Make not found" && exit 1)
	@echo "✅ Make found"
	@if command -v zip >/dev/null 2>&1; then echo "✅ Zip found"; else echo "⚠️  Zip not found (Windows packaging will fail)"; fi
	@if command -v tar >/dev/null 2>&1; then echo "✅ Tar found"; else echo "❌ Tar not found"; fi

# MARK: Quick build for current platform
.PHONY: quick
quick: restore
	@echo "⚡ Quick build for current platform..."
	dotnet publish $(PROJECT_NAME).csproj \
		--configuration $(CONFIGURATION) \
		--self-contained true \
		--output $(BUILD_DIR)/quick \
		/p:PublishSingleFile=true
	@echo "✅ Quick build complete: $(BUILD_DIR)/quick/$(PROJECT_NAME)"

# MARK: Help
.PHONY: help
help: info

.DEFAULT_GOAL := info