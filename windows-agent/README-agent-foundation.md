# Rent Device Agent

This is the Windows client foundation. Place the administrator-generated
`device-agent.json` beside the published executable, then publish with:

```powershell
dotnet publish -c Release
```

The agent registers once, stores its issued token under `ProgramData`, sends
hardware heartbeats, and keeps an offline log. The next implementation step is
the signed Windows Service installer and command execution for lock/unlock,
Return mode, Maintenance mode, Lost mode, snapshots, and health alerts.
