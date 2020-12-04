﻿using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.XPath;
using CommandLine;
using KeeperSecurity.Authentication;
using KeeperSecurity.Configuration;
using KeeperSecurity.OfflineStorage.Sqlite;
using KeeperSecurity.Utils;
using Org.BouncyCastle.Tsp;

namespace EnterpriseBackup
{
    public interface ICommand
    {
        int Order { get; }
        string Description { get; }

        Task ExecuteCommand(string args);
    }

    public class SimpleCommand : ICommand
    {
        public int Order { get; set; }
        public string Description { get; set; }
        public Func<string, Task> Action { get; set; }
        public async Task ExecuteCommand(string args)
        {
            if (Action != null)
            {
                await Action(args);
            }
        }
    }

    public static class CommandExtensions
    {
        public static bool IsWhiteSpace(char ch)
        {
            return char.IsWhiteSpace(ch);
        }

        public static bool IsPathDelimiter(char ch)
        {
            return ch == '/';
        }

        public static IEnumerable<string> TokenizeArguments(this string args)
        {
            return TokenizeArguments(args, IsWhiteSpace);
        }

        public static IEnumerable<string> TokenizeArguments(this string args, Func<char, bool> isDelimiter)
        {
            var sb = new StringBuilder();
            var pos = 0;
            var isQuote = false;
            var isEscape = false;
            while (pos < args.Length)
            {
                var ch = args[pos];

                if (isEscape)
                {
                    isEscape = false;
                    sb.Append(ch);
                }
                else
                {
                    switch (ch)
                    {
                        case '\\':
                            isEscape = true;
                            break;
                        case '"':
                            isQuote = !isQuote;
                            break;
                        default:
                        {
                            if (!isQuote && isDelimiter(ch))
                            {
                                if (sb.Length > 0)
                                {
                                    yield return sb.ToString();
                                    sb.Length = 0;
                                }
                            }
                            else
                            {
                                sb.Append(ch);
                            }

                            break;
                        }
                    }
                }

                pos++;
            }

            if (sb.Length > 0)
            {
                yield return sb.ToString();
            }
        }
    }

    public class ParsableCommand<T> : ICommand where T : class
    {
        public int Order { get; internal set; }
        public string Description { get; set; }
        public Func<T, Task> Action { get; internal set; }

        public async Task ExecuteCommand(string args)
        {
            var res = Parser.Default.ParseArguments<T>(args.TokenizeArguments());
            T options = null;
            res.WithParsed(o => { options = o; });
            if (options != null)
            {
                await Action(options);
            }
        }
    }

    public class CliCommands
    {
        public IDictionary<string, ICommand> Commands { get; } = new Dictionary<string, ICommand>();
        public IDictionary<string, string> CommandAliases { get; } = new Dictionary<string, string>();
    }

    public sealed class MainLoop : CliCommands
    {
        public MainLoop()
        {
            Commands.Add("clear",
                new SimpleCommand
                {
                    Order = 1000,
                    Description = "Clears the screen",
                    Action = (args) =>
                    {
                        Console.Clear();
                        return Task.FromResult(true);
                    }
                });

            Commands.Add("quit",
                new SimpleCommand
                {
                    Order = 1001,
                    Description = "Quit",
                    Action = (args) =>
                    {
                        Finished = true;
                        StateContext = null;
                        Environment.Exit(0);
                        return Task.FromResult(true);
                    }
                });
            CommandAliases.Add("c", "clear");
            CommandAliases.Add("q", "quit");
        }

        public StateCommands StateContext { get; set; }
        public bool Finished { get; set; }
        public Queue<string> CommandQueue { get; } = new Queue<string>();

        public async Task Run()
        {
            while (!Finished)
            {
                if (StateContext == null) break;
                if (StateContext.NextStateCommands != null)
                {
                    if (!ReferenceEquals(StateContext, StateContext.NextStateCommands))
                    {
                        var oldContext = StateContext;
                        StateContext = oldContext.NextStateCommands;
                        oldContext.NextStateCommands = null;
                        oldContext.Dispose();
                    }
                    else
                    {
                        StateContext.NextStateCommands = null;
                    }

                    Program.GetInputManager().ClearHistory();
                }

                string command;
                if (CommandQueue.Count > 0)
                {
                    command = CommandQueue.Dequeue();
                }
                else
                {
                    Console.Write(StateContext.GetPrompt() + "> ");
                    command = await Program.GetInputManager().ReadLine(new ReadLineParameters
                    {
                        IsHistory = true
                    });
                }

                if (string.IsNullOrEmpty(command)) continue;

                command = command.Trim();
                var parameter = "";
                var pos = command.IndexOf(' ');
                if (pos > 1)
                {
                    parameter = command.Substring(pos + 1).Trim();
                    command = command.Substring(0, pos).Trim();
                }

                command = command.ToLowerInvariant();
                if (CommandAliases.TryGetValue(command, out var fullCommand))
                {
                    command = fullCommand;
                }
                else if (StateContext.CommandAliases.TryGetValue(command, out fullCommand))
                {
                    command = fullCommand;
                }

                if (!Commands.TryGetValue(command, out var cmd))
                {
                    StateContext.Commands.TryGetValue(command, out cmd);
                }

                if (cmd != null)
                {
                    try
                    {
                        await cmd.ExecuteCommand(parameter);
                    }
                    catch (Exception e)
                    {
                        if (!await StateContext.ProcessException(e))
                        {
                            Console.WriteLine("Error: " + e.Message);
                        }
                    }
                }
                else
                {
                    if (command != "?")
                    {
                        Console.WriteLine($"Invalid command: {command}");
                    }
                    var tab = new Tabulate(3);
                    tab.AddHeader("Command", "Alias", "Description");
                    foreach (var c in (Commands.Concat(StateContext.Commands))
                        .OrderBy(x => x.Value.Order))
                    {
                        var alias = CommandAliases
                            .Where(x => x.Value == c.Key)
                            .Select(x => x.Key)
                            .FirstOrDefault();
                        if (alias == null)
                        {
                            alias = StateContext.CommandAliases
                                .Where(x => x.Value == c.Key)
                                .Select(x => x.Key)
                                .FirstOrDefault();
                        }
                        tab.AddRow(c.Key, alias ?? "", c.Value.Description);
                    }
                    tab.DumpRowNo = false;
                    tab.LeftPadding = 1;
                    tab.Dump();
                }

                Console.WriteLine();
            }
        }

    }

    public abstract class StateCommands : CliCommands, IDisposable
    {
        public abstract string GetPrompt();

        public virtual Task<bool> ProcessException(Exception e)
        {
            return Task.FromResult(false);
        }

        public StateCommands NextStateCommands { get; set; }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                NextStateCommands = null;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
    internal partial class MainMenuCliContext : StateCommands
    {
        public MainMenuCliContext()
        {
            var keeperLocation = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), ".keeper");
            if (!Directory.Exists(keeperLocation))
            {
                Directory.CreateDirectory(keeperLocation);
            }
            var configFile = Path.Combine(keeperLocation, "backup.json");
            var cache = new JsonConfigurationCache(new JsonConfigurationFileLoader(configFile));
            Storage = new JsonConfigurationStorage(cache);
            if (string.IsNullOrEmpty(Storage.LastServer))
            {
                KeeperServer = KeeperEndpoint.DefaultKeeperServer;
                Console.WriteLine($"Connecting to the default Keeper sever: {KeeperServer}");
            }
            else
            {
                KeeperServer = Storage.LastServer;
            }
            BackupLocation = Path.Combine(keeperLocation, "Backups");
            if (!Directory.Exists(BackupLocation))
            {
                Directory.CreateDirectory(BackupLocation);
            }

            Commands.Add("server",
                new SimpleCommand
                {
                    Order = 10,
                    Description = "Gets or sets Keeper server.",
                    Action = (arguments) =>
                    {
                        if (!string.IsNullOrEmpty(arguments))
                        {
                            KeeperServer = arguments;
                        }
                        Console.WriteLine($"Keeper server: {KeeperServer}");
                        return Task.CompletedTask;
                    },
                });

            Commands.Add("backup-dir",
                new SimpleCommand
                {
                    Order = 11,
                    Description = "Gets or sets backup file(s) directory.",
                    Action = (arguments) =>
                    {
                        if (!string.IsNullOrEmpty(arguments))
                        {
                            if (!Directory.Exists(arguments))
                            {
                                Directory.CreateDirectory(arguments);
                            }

                            BackupLocation = arguments;
                        }
                        Console.WriteLine($"Backup file location: {BackupLocation}");
                        return Task.CompletedTask;
                    },
                });
            CommandAliases.Add("bd", "backup-dir");

            Commands.Add("backup-list",
                new SimpleCommand
                {
                    Order = 12,
                    Description = "Lists backup files.",
                    Action = ListBackupFiles,
                });
            CommandAliases.Add("bl", "backup-list");

            Commands.Add("backup-new",
                new ParsableCommand<CreateBackupOptions>
                {
                    Order = 20,
                    Description = "Creates a backup file.",
                    Action = CreateBackup,
                });
            CommandAliases.Add("bn", "backup-new");

            Commands.Add("backup-unlock",
                new ParsableCommand<BackupOptions>
                {
                    Order = 21,
                    Description = "Selects and unlocks a backup file.",
                    Action = UnlockBackup,
                });
            CommandAliases.Add("bu", "backup-unlock");
        }

        private async Task ListBackupFiles(string arguments)
        {
            var tab = new Tabulate(4);
            tab.AddHeader("Backup Name", "Created", "Author", "Admins");
            foreach (var file in Directory.EnumerateFiles(BackupLocation))
            {
                try
                {
                    var info = new Dictionary<string, string>();
                    {
                        await using var connection = new SQLiteConnection($"Data Source={file};");
                        connection.Open();
                        var isValid = DatabaseUtils.VerifyDatabase(false,
                            connection,
                            new[] {typeof(BackupRecord), typeof(BackupUser), typeof(BackupAdminKey), typeof(BackupInfo)},
                            null);
                        if (!isValid) continue;
                        var adminStorage = new BackupDataReader<BackupInfo>(() => connection);
                        foreach (var pair in adminStorage.GetAll())
                        {
                            if (!string.IsNullOrEmpty(pair.Name) && !string.IsNullOrEmpty(pair.Value))
                            {
                                info[pair.Name] = pair.Value;
                            }
                        }
                    }
                    var admins = info.ContainsKey("BackupAdmins") ? info["BackupAdmins"].Split('\n') : new string[0];
                    for (var i = 0; i < Math.Max(1, admins.Length); i++)
                    {
                        if (i == 0)
                        {
                            var name = Path.GetFileName(file);
                            if (name.EndsWith(".backup"))
                            {
                                name = name.Substring(0, name.Length - ".backup".Length);
                            }

                            if (!info.TryGetValue("BackupDate", out var unixDate)) unixDate = "";
                            if (!string.IsNullOrEmpty(unixDate))
                            {
                                if (int.TryParse(unixDate, out var unix))
                                {
                                    var date = DateTimeOffset.FromUnixTimeSeconds(unix);
                                    unixDate = date.ToString("s");
                                }
                            }

                            tab.AddRow(name, unixDate, 
                                info.TryGetValue("BackupAuthor", out var author) ? author : "",
                                admins.Length > 0 ? admins[0] : "");
                        }
                        else
                        {
                            tab.AddRow("", "", "", admins[i]);
                        }
                    }

                    tab.DumpRowNo = false;
                    tab.Dump();
                }
                catch (Exception e)
                {
                    Debug.WriteLine(e.Message);
                }

            }
        }

        public JsonConfigurationStorage Storage { get; set; }

        public string KeeperServer { get; set; }
        public string BackupLocation { get; set; }

        public override string GetPrompt()
        {
            return "Main Menu";
        }
    }

    internal class BackupOptions
    {
        [Value(0, Required = true, MetaName = "name", HelpText = "Backup file name.")]
        public string Name { get; set; }
    }

    internal class CreateBackupOptions : BackupOptions
    {
        [Option("admin", Required = false, HelpText = "Backup Administrator account.")]
        public string AdminAccount { get; set; }
    }

}