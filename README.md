# WORK IN PROGRESS

# FinDLNA - Jellyfin DLNA Proxy Server

A lightweight DLNA Media Server proxy that runs on Raspberry Pi and exposes remote Jellyfin server libraries as a local UPnP/DLNA MediaServer. This allows DLNA-compatible devices (smart TVs, game consoles, media players) to browse and stream content from your Jellyfin server without direct network access to it.

## Overview

FinDLNA acts as a bridge between your Jellyfin media server and DLNA clients on your local network. The Raspberry Pi handles SSDP broadcasts, UPnP service discovery, and content directory browsing while proxying all media streams from the remote Jellyfin server.

## Key Features

- **Zero Local Storage**: No caching of media files or artwork - everything is streamed directly from Jellyfin
- **Transcoding Delegation**: All heavy processing (transcoding, format conversion) is handled by the Jellyfin server
- **Web Configuration**: Simple web interface for setting up Jellyfin connection details
- **Device Profile Support**: Uses Jellyfin's device profile system for client compatibility
- **Standard DLNA Compliance**: Works with any UPnP/DLNA compatible device

## How It Works

### Architecture

1. **SSDP Discovery**: Broadcasts UPnP MediaServer announcements on the local network
2. **Content Directory Service**: Handles DLNA browse requests and returns DIDL-Lite formatted metadata
3. **Media Streaming Proxy**: Proxies media streams from Jellyfin to DLNA clients
4. **Jellyfin Integration**: Uses official Jellyfin C# SDK for all server interactions

### Data Flow

1. DLNA client discovers FinDLNA server via SSDP
2. Client browses content through UPnP ContentDirectory service
3. FinDLNA fetches library structure and metadata from Jellyfin
4. When client requests media, FinDLNA:
   - Calls Jellyfin's PlaybackInfo API to get stream URL
   - Opens connection to Jellyfin stream (direct or transcoded)
   - Proxies bytes to DLNA client in real-time
   - Reports playback progress back to Jellyfin

## Setup

### Requirements

- Raspberry Pi (any model with network connectivity)
- .NET 9.0 runtime
- Access to a Jellyfin server
- Local network with multicast support

### Installation

1. Clone the repository
2. Copy `appsettings.example.json` to `appsettings.json`
3. Build the project: `dotnet build`
4. Run: `dotnet run`
5. Open web browser to `http://pi-ip:5000`
6. Configure Jellyfin server connection

### Configuration

Access the web interface at `http://localhost:5000` to configure:

- **Jellyfin Server URL**: Full URL to your Jellyfin instance
- **Username**: Jellyfin username (password optional for public users)
- **Password**: Jellyfin password (if required)

The system will authenticate with Jellyfin and save connection details. The DLNA service starts automatically once configured.

## Network Ports

- **5000**: Web configuration interface
- **8200**: DLNA HTTP service (device description, content directory, streaming)
- **1900**: SSDP multicast discovery (UDP)

## Project Structure

### Core Services

- **SsdpService**: Handles UPnP device discovery and announcements
- **ContentDirectoryService**: Implements DLNA content browsing with DIDL-Lite responses
- **StreamingService**: Proxies media streams from Jellyfin to clients
- **JellyfinService**: Wraps Jellyfin SDK for all server interactions
- **AuthService**: Manages Jellyfin authentication and configuration storage

### UPnP/DLNA Implementation

- Device description XML with MediaServer profile
- ContentDirectory service with Browse, Search, and Sort capabilities
- ConnectionManager service for protocol negotiation
- SOAP request/response handling for all UPnP actions

## Supported Media Types

FinDLNA supports any media format that Jellyfin can serve:

- **Video**: MP4, AVI, MKV, MOV, etc.
- **Audio**: MP3, FLAC, AAC, OGG, etc.
- **Images**: JPEG, PNG, GIF, etc.

Format compatibility depends on the DLNA client's capabilities. Jellyfin handles transcoding automatically based on device profiles.

## Technical Details

### DLNA Protocol Compliance

- UPnP MediaServer:1 device specification
- ContentDirectory:1 service with DIDL-Lite metadata
- ConnectionManager:1 service for stream negotiation
- SSDP discovery with proper alive/byebye notifications

### Security

- Configuration stored in protected JSON file
- Jellyfin access tokens used for all API calls
- No sensitive data cached locally
- Web interface restricted to configuration only

## Limitations

- Requires Jellyfin server for all functionality
- No offline capability or local media support
- Dependent on network connectivity to Jellyfin server
- Limited to Jellyfin-supported media formats and codecs

## Troubleshooting

Check logs for detailed debugging information. Common issues:

- **DLNA devices not discovering server**: Verify multicast/SSDP on network
- **Authentication failures**: Check Jellyfin credentials and server accessibility  
- **Streaming issues**: Confirm Jellyfin transcoding settings and client compatibility
- **Web interface not accessible**: Verify port 5000 is available and firewall settings

## Development

Built with:

- .NET 9.0 and ASP.NET Core
- Jellyfin SDK for server integration
- Standard UPnP/DLNA protocols
- Minimal web interface with vanilla HTML/CSS/JavaScript

## License

This project is licensed under the MIT License.
