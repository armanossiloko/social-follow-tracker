// Windowless entry point for the scheduled daily run. Everything goes to following.log.
using Following.Core;

var args_ = args.ToList();
var paths = AppPaths.FromArgs(args_).EnsureCreated();
var log = new Log(paths.Log, console: false);
try
{
    return await new Jobs(paths, log).DailyAsync();
}
catch (Exception e)
{
    log.Error($"Daily run crashed: {e}");
    return 2;
}
