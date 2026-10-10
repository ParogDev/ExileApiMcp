using ExileApiMcp.Supervisor;

// The process an MCP client actually launches (run.cmd): it runs the ExileApiMcp server as a child and swaps it for a
// newly deployed build without the client noticing. See Supervisor/README.md.
SupervisorOptions opts;
try { opts = SupervisorOptions.Parse(args); }
catch (ArgumentException ex) { Console.Error.WriteLine($"[supervisor] {ex.Message}"); return 2; }
return opts.Http ? await new HttpSupervisor(opts).RunAsync() : await new StdioSupervisor(opts).RunAsync();
