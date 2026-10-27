# Troubleshootr

The first tool you open when a Windows PC has a problem. Free, open source and portable: run it from a USB stick, click
**Quick scan**, and get every common problem ranked by severity, with the evidence and what to do. It covers drive
health, RAM, crashes and blue screens, drivers, updates, startup, network, security, battery and more. Full hardware and
system details sit in tabs.

Built for technicians and enthusiasts. Windows 10 and 11 (Windows 7/8.1 best effort). No install, nothing left behind.

**Website:** https://glr-p.github.io/Troubleshootr/ (screenshots and features). **Download:** `Troubleshootr.exe` from the [latest release](https://github.com/GLr-P/Troubleshootr/releases/latest): one file, nothing to install. The zip on the same page adds `troubleshootr-cli.exe` for scripted scans. See the [check catalogue](docs/CHECKS.md) for everything it looks at.

It isn't code-signed, so the first time Windows may say "Windows protected your PC": click **More info → Run anyway**.

It only changes the PC when you click a fix on the Fixes tab (system file repair, temp cleanup, DNS flush, network reset, print queue, RAM test); each one asks first and is logged in `Troubleshootr-actions.log` and the report.

License: GPL-3.0.
