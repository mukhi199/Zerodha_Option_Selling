#!/bin/bash

echo "=========================================================="
echo "  🚀 STARTING ZERODHA ALGORITHMIC ENGINE"
echo "=========================================================="

cd "$(dirname "$0")"

# Kill any existing localtunnel processes running on port 5000 to prevent port binding collisions
pkill -f "localtunnel"

# Spin up the LocalTunnel in the background using a permanently fixed subdomain
echo "Starting permanent Webhook Tunnel (lenovo-zerodha)..."
npx --yes localtunnel --port 5000 --subdomain lenovo-zerodha > /dev/null 2>&1 &

echo "Tunnel established. Webhooks are natively routing to:"
echo "👉 https://lenovo-zerodha.loca.lt/zerodha/postback"
echo "=========================================================="

# Start the C# strategy daemon
cd "Trading.Strategy"
dotnet run
