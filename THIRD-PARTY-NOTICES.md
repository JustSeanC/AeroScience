# Third-party components

The AeroScience license covers original project code, not Microsoft SDK files or third-party runtimes.

- .NET / ASP.NET Core and Microsoft.Data.Sqlite: Microsoft / .NET Foundation, MIT and associated runtime notices. A self-contained publish retains its runtime `LICENSE.txt` and `ThirdPartyNotices.txt` when supplied by the runtime pack.
- SQLitePCLRaw (Eric Sink / contributors): Apache-2.0. See <https://github.com/ericsink/SQLitePCL.raw>. Its native SQLite distribution is governed by the supplied package notices; SQLite itself is public domain (<https://sqlite.org/copyright.html>).
- Playwright: Microsoft, Apache-2.0; development/test tooling, not a runtime dependency of the companion. Test packages are not included as application dependencies.
- MSFS SDK, SimConnect runtime, and simulator-owned JS/templates: Microsoft proprietary terms apply. No SDK binary or simulator-owned script/template is checked into this repository or included in the companion preview. Review the installed official SDK terms before redistributing any runtime file.

SDK research also consulted Microsoft's published avionics types and community examples linked in `docs/toolbar.md`. No external application implementation was vendored. The project loads the simulator's own templates and CommBus service at runtime; those files are not rehosted by AeroScience.
