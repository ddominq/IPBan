﻿/*
MIT License

Copyright (c) 2019 Digital Ruby, LLC - https://www.digitalruby.com

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DigitalRuby.IPBan
{
    [RequiredOperatingSystem(IPBanOS.Linux)]
    [CustomName("Default")]
    public class IPBanLinuxFirewall : IPBanBaseFirewall, IIPBanFirewall
    {
        /// <summary>
        /// IPV6 firewall
        /// </summary>
        private readonly IPBanLinuxFirewall6 firewall6;

        private const string inetFamily = "inet"; // firewall6 takes care of inet6
        private const int hashSize = 1024;
        private const int blockRuleMaxCount = 2097152;
        private const int allowRuleMaxCount = 65536;
        private const int blockRuleRangesMaxCount = 4194304;

        private HashSet<uint> bannedIPAddresses;
        private HashSet<uint> allowedIPAddresses;

        private string GetSetFileName(string ruleName)
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ruleName + ".set");
        }

        private string GetTableFileName()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ipban.tbl");
        }

        private int RunProcess(string program, bool requireExitCode, string commandLine, params object[] args)
        {
            return RunProcess(program, requireExitCode, out _, commandLine, args);
        }

        private int RunProcess(string program, bool requireExitCode, out IReadOnlyList<string> lines, string commandLine, params object[] args)
        {
            commandLine = string.Format(commandLine, args);
            string bash = "-c \"" + program + " " + commandLine.Replace("\"", "\\\"") + "\"";
            IPBanLog.Debug("Running firewall process: {0} {1}", program, commandLine);
            using (Process p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    Arguments = bash,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                }
            })
            {
                p.Start();
                List<string> lineList = new List<string>();
                string line;
                while ((line = p.StandardOutput.ReadLine()) != null)
                {
                    lineList.Add(line);
                }
                lines = lineList;
                if (!p.WaitForExit(60000))
                {
                    IPBanLog.Error("Process {0} {1} timed out", program, commandLine);
                    p.Kill();
                }
                if (requireExitCode && p.ExitCode != 0)
                {
                    IPBanLog.Error("Process {0} {1} had exit code {2}", program, commandLine, p.ExitCode);
                }
                return p.ExitCode;
            }
        }

        private void DeleteSet(string ruleName)
        {
            RunProcess("ipset", true, out IReadOnlyList<string> lines, "list -n");
            foreach (string line in lines)
            {
                if (line.Trim().Equals(ruleName, StringComparison.OrdinalIgnoreCase))
                {
                    // remove set
                    RunProcess("ipset", true, $"destroy {ruleName}");
                    string setFileName = GetSetFileName(ruleName);
                    if (File.Exists(setFileName))
                    {
                        File.Delete(setFileName);
                    }
                    break;
                }
            }
        }

        private void SaveTableToDisk()
        {
            // persist table rules, this file is tiny so no need for a temp file and then move
            string tableFileName = GetTableFileName();
            RunProcess("iptables-save", true, $"> \"{tableFileName}\"");
        }

        private bool CreateOrUpdateRule(string ruleName, string action, string hashType, int maxCount, IEnumerable<PortRange> allowedPorts, CancellationToken cancelToken)
        {
            // ensure that a set exists for the iptables rule in the event that this is the first run
            RunProcess("ipset", true, $"create {ruleName} hash:{hashType} family {inetFamily} hashsize {hashSize} maxelem {maxCount} -exist");
            if (cancelToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancelToken);
            }

            string setFileName = GetSetFileName(ruleName);
            if (!File.Exists(setFileName))
            {
                RunProcess("ipset", true, $"save {ruleName} > \"{setFileName}\"");
            }

            if (cancelToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancelToken);
            }

            PortRange[] allowedPortsArray = allowedPorts?.ToArray();

            // create or update the rule in iptables
            RunProcess("iptables", true, out IReadOnlyList<string> lines, "-L --line-numbers");
            string portString = " ";
            bool replaced = false;
            if (allowedPortsArray != null && allowedPortsArray.Length != 0)
            {
                string portList = (action == "DROP" ? IPBanFirewallUtility.GetPortRangeStringBlockExcept(allowedPorts) :
                     IPBanFirewallUtility.GetPortRangeStringAllow(allowedPorts));
                portString = " -m multiport --dports " + portList.Replace('-', ':') + " "; // iptables uses ':' instead of '-' for range
            }
            string ruleNameWithSpaces = " " + ruleName + " ";
            foreach (string line in lines)
            {
                if (line.Contains(ruleNameWithSpaces, StringComparison.OrdinalIgnoreCase))
                {
                    // rule number is first piece of the line
                    int index = line.IndexOf(' ');
                    int ruleNum = int.Parse(line.Substring(0, index));

                    // replace the rule with the new info
                    RunProcess("iptables", true, $"-R INPUT {ruleNum} -m set{portString}--match-set \"{ruleName}\" src -j {action}");
                    replaced = true;
                    break;
                }
            }
            if (!replaced)
            {
                // add a new rule
                RunProcess("iptables", true, $"-A INPUT -m set{portString}--match-set \"{ruleName}\" src -j {action}");
            }

            if (cancelToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancelToken);
            }

            SaveTableToDisk();

            return true;
        }

        private HashSet<uint> LoadIPAddresses(string ruleName, string action, string hashType, int maxCount)
        {
            HashSet<uint> ipAddresses = new HashSet<uint>();

            try
            {
                if (hashType != "ip")
                {
                    throw new ArgumentException("Can only load hash of type 'ip'");
                }

                CreateOrUpdateRule(ruleName, action, hashType, maxCount, null, default);

                // copy ip addresses from the rule to the set
                string fileName = GetSetFileName(ruleName);
                if (File.Exists(fileName))
                {
                    uint value;
                    foreach (string line in File.ReadLines(fileName).Skip(1))
                    {
                        string[] pieces = line.Split(' ');
                        if (pieces.Length > 2 && pieces[0] == "add" && (value = IPBanFirewallUtility.ParseIPV4(pieces[2])) != 0)
                        {
                            ipAddresses.Add(value);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                IPBanLog.Error(ex);
            }

            return ipAddresses;
        }

        private void DeleteFile(string fileName)
        {
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    File.Delete(fileName);
                    break;
                }
                catch
                {
                    Task.Delay(20).Wait();
                }
            }
        }

        // deleteRule will drop the rule and matching set before creating the rule and set, use this is you don't care to update the rule and set in place
        private HashSet<uint> UpdateRule(string ruleName, string action, IEnumerable<string> ipAddresses,
            HashSet<uint> existingIPAddresses, string hashType, int maxCount, bool deleteRule, IEnumerable<PortRange> allowPorts, CancellationToken cancelToken,
            out bool result)
        {
            string ipFile = GetSetFileName(ruleName);
            string ipFileTemp = ipFile + ".tmp";
            HashSet<uint> newIPAddressesUint = new HashSet<uint>();
            uint value = 0;

            // add and remove the appropriate ip addresses from the set
            using (StreamWriter writer = File.CreateText(ipFileTemp))
            {
                if (cancelToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancelToken);
                }
                writer.WriteLine($"create {ruleName} hash:{hashType} family {inetFamily} hashsize {hashSize} maxelem {maxCount} -exist");
                foreach (string ipAddress in ipAddresses)
                {
                    if (cancelToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(cancelToken);
                    }

                    // only allow ipv4 for now
                    value = 0;
                    if (IPAddressRange.TryParse(ipAddress, out IPAddressRange range) &&
                        range.Begin.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                        range.End.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                        // if deleting the rule, don't track the uint value
                        (!deleteRule || (value = IPBanFirewallUtility.ParseIPV4(ipAddress)) != 0))
                    {
                        try
                        {
                            if (range.Begin.Equals(range.End))
                            {
                                writer.WriteLine($"add {ruleName} {range.Begin} -exist");
                            }
                            else
                            {
                                writer.WriteLine($"add {ruleName} {range.ToCidrString()} -exist");
                            }
                            if (!deleteRule)
                            {
                                newIPAddressesUint.Add((value == 0 ? IPBanFirewallUtility.ParseIPV4(ipAddress) : value));
                            }
                        }
                        catch
                        {
                            // ignore invalid cidr ranges
                        }
                    }
                }

                // if the rule was deleted, no need to add del entries
                if (!deleteRule)
                {
                    // for ip that dropped out, remove from firewall
                    foreach (uint droppedIP in existingIPAddresses.Where(e => newIPAddressesUint.Contains(e)))
                    {
                        writer.WriteLine($"del {ruleName} {droppedIP.ToIPAddress()} -exist");
                    }
                }
            }

            if (cancelToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancelToken);
            }
            else
            {
                // TODO: Is there an easier way to move to a file that exists?
                if (File.Exists(ipFile))
                {
                    DeleteFile(ipFile);
                }
                File.Move(ipFileTemp, ipFile);

                if (deleteRule)
                {
                    DeleteRule(ruleName);
                }

                // restore the file to get the set updated
                result = (RunProcess("ipset", true, $"restore < \"{ipFile}\"") == 0);

                // ensure rule exists for the set
                CreateOrUpdateRule(ruleName, action, hashType, maxCount, allowPorts, cancelToken);
            }

            return newIPAddressesUint;
        }

        internal static void RemoveAllTablesAndSets()
        {
            try
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                foreach (string setFile in Directory.GetFiles(dir, "*.set")
                    .Union(Directory.GetFiles(dir, "*.tbl")
                    .Union(Directory.GetFiles(dir, "*.set6"))
                    .Union(Directory.GetFiles(dir, "*.tbl6"))))
                {
                    File.Delete(setFile);
                }
            }
            catch
            {
            }
        }

        private void MigrateOldDefaultRuleNames()
        {
            string oldVersionFile = GetSetFileName(RulePrefix + "0");
            if (File.Exists(oldVersionFile))
            {
                RemoveAllTablesAndSets();
            }
        }

        public IPBanLinuxFirewall(string rulePrefix = null) : base(rulePrefix)
        {
            MigrateOldDefaultRuleNames();
            firewall6 = new IPBanLinuxFirewall6(RulePrefix + "v6_");

            /*
            // restore existing sets from disk
            RunProcess("ipset", true, out IReadOnlyList<string> existingSets, $"-L | grep ^Name:");
            foreach (string set in existingSets.Where(s => s.StartsWith("Name: " + RulePrefix, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.Substring("Name: ".Length)))
            {
                RunProcess("ipset", true, $"flush {set}");
            }
            */

            foreach (string setFile in Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory, "*.set"))
            {
                RunProcess("ipset", true, $"restore < \"{setFile}\"");
            }

            allowedIPAddresses = LoadIPAddresses(AllowRuleName, "ACCEPT", "ip", allowRuleMaxCount);
            bannedIPAddresses = LoadIPAddresses(BlockRuleName, "DROP", "ip", blockRuleMaxCount);

            // restore existing rules from disk
            string ruleFile = GetTableFileName();
            if (File.Exists(ruleFile))
            {
                RunProcess("iptables-restore", true, $"< \"{ruleFile}\"");
            }
        }

        public async Task<bool> BlockIPAddresses(string ruleNamePrefix, IEnumerable<string> ipAddresses, CancellationToken cancelToken = default)
        {
            bool result = await firewall6.BlockIPAddresses(ruleNamePrefix, ipAddresses, cancelToken);
            if (!result)
            {
                return false;
            }

            try
            {
                string ruleName = (string.IsNullOrWhiteSpace(ruleNamePrefix) ? BlockRuleName : RulePrefix + ruleNamePrefix);
                bannedIPAddresses = UpdateRule(ruleName, "DROP", ipAddresses, bannedIPAddresses, "ip", blockRuleMaxCount, false, null, cancelToken, out result);
                return result;
            }
            catch (Exception ex)
            {
                IPBanLog.Error(ex);
                return false;
            }
        }

        public Task<bool> BlockIPAddressesDelta(string ruleNamePrefix, IEnumerable<IPBanFirewallIPAddressDelta> ipAddresses, CancellationToken cancelToken = default)
        {
            List<IPBanFirewallIPAddressDelta> deltas = new List<IPBanFirewallIPAddressDelta>(ipAddresses.Where(i => IPAddress.Parse(i.IPAddress).AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork));
            List<IPBanFirewallIPAddressDelta> delta6 = new List<IPBanFirewallIPAddressDelta>(ipAddresses.Where(i => IPAddress.Parse(i.IPAddress).AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6));
            string ruleName = (string.IsNullOrWhiteSpace(ruleNamePrefix) ? BlockRuleName : RulePrefix + ruleNamePrefix);
            bool changed = false;
            foreach (IPBanFirewallIPAddressDelta delta in deltas)
            {
                if (IPAddress.TryParse(delta.IPAddress, out IPAddress ipObj))
                {
                    if (delta.Added)
                    {
                        changed = bannedIPAddresses.Add(ipObj.ToUInt32());
                    }
                    else
                    {
                        changed = bannedIPAddresses.Remove(ipObj.ToUInt32());
                    }
                }
            }
            if (changed)
            {
                bannedIPAddresses = UpdateRule(ruleName, "DROP", bannedIPAddresses.Select(b => b.ToIPAddress().ToString()), bannedIPAddresses, "ip", blockRuleMaxCount, false, null, cancelToken, out bool result);
                if (!result)
                {
                    return Task.FromResult(false);
                }
            }
            return firewall6.BlockIPAddressesDelta(ruleNamePrefix, delta6, cancelToken);
        }

        public async Task<bool> BlockIPAddresses(string ruleNamePrefix, IEnumerable<IPAddressRange> ranges, IEnumerable<PortRange> allowedPorts, CancellationToken cancelToken = default)
        {
            bool result = await firewall6.BlockIPAddresses(ruleNamePrefix, ranges, allowedPorts, cancelToken);
            if (!result)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(ruleNamePrefix))
            {
                return false;
            }

            try
            {
                string ruleName = RulePrefix + "_" + ruleNamePrefix + "_0";
                UpdateRule(ruleName, "DROP", ranges.Select(r => r.ToCidrString()), null, "net", blockRuleRangesMaxCount, true, allowedPorts, cancelToken, out result);
                return result;
            }
            catch (Exception ex)
            {
                IPBanLog.Error(ex);
                return false;
            }
        }

        public async Task UnblockIPAddresses(IEnumerable<string> ipAddresses)
        {
            await firewall6.UnblockIPAddresses(ipAddresses);

            bool changed = false;
            foreach (string ipAddress in ipAddresses)
            {
                uint ipValue = IPBanFirewallUtility.ParseIPV4(ipAddress);
                if (ipValue != 0 && !string.IsNullOrWhiteSpace(ipAddress) && RunProcess("ipset", true, $"del {BlockRuleName} {ipAddress} -exist") == 0)
                {
                    bannedIPAddresses.Remove(ipValue);
                    changed = true;
                }
            }
            if (changed)
            {
                RunProcess("ipset", true, $"save {BlockRuleName} > \"{GetSetFileName(BlockRuleName)}\"");
            }
        }

        public async Task<bool> AllowIPAddresses(IEnumerable<string> ipAddresses, CancellationToken cancelToken = default)
        {
            bool result = await firewall6.AllowIPAddresses(ipAddresses, cancelToken);
            if (!result)
            {
                return false;
            }

            try
            {
                allowedIPAddresses = UpdateRule(AllowRuleName, "ACCEPT", ipAddresses, allowedIPAddresses, "ip", allowRuleMaxCount, false, null, cancelToken, out result);
                return result;
            }
            catch (Exception ex)
            {
                IPBanLog.Error(ex);
                return false;
            }
        }

        public IEnumerable<string> GetRuleNames(string ruleNamePrefix = null)
        {
            const string setText = " match-set ";
            string prefix = setText + RulePrefix + (ruleNamePrefix ?? string.Empty);
            RunProcess("iptables", true, out IReadOnlyList<string> lines, "-L");
            foreach (string line in lines)
            {
                int pos = line.IndexOf(prefix);
                if (pos >= 0)
                {
                    pos += setText.Length;
                    int start = pos;
                    while (++pos < line.Length && line[pos] != ' ') { }
                    yield return line.Substring(start, pos - start);
                }
            }
            foreach (string name in firewall6.GetRuleNames())
            {
                yield return name;
            }
        }

        public bool RuleExists(string ruleName)
        {
            RunProcess("iptables", true, out IReadOnlyList<string> lines, "-L --line-numbers");
            string ruleNameWithSpaces = " " + ruleName + " ";
            foreach (string line in lines)
            {
                if (line.Contains(ruleNameWithSpaces, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return firewall6.RuleExists(ruleName);
        }

        public bool DeleteRule(string ruleName)
        {
            RunProcess("iptables", true, out IReadOnlyList<string> lines, "-L --line-numbers");
            string ruleNameWithSpaces = " " + ruleName + " ";
            foreach (string line in lines)
            {
                if (line.Contains(ruleNameWithSpaces, StringComparison.OrdinalIgnoreCase))
                {
                    // rule number is first piece of the line
                    int index = line.IndexOf(' ');
                    int ruleNum = int.Parse(line.Substring(0, index));

                    // remove the rule from iptables
                    RunProcess("iptables", true, $"-D INPUT {ruleNum}");
                    SaveTableToDisk();

                    // remove the set
                    DeleteSet(ruleName);

                    return true;
                }
            }

            return firewall6.DeleteRule(ruleName);
        }

        public IEnumerable<string> EnumerateBannedIPAddresses()
        {
            return bannedIPAddresses.Select(b => b.ToIPAddress().ToString())
                .Union(firewall6.EnumerateBannedIPAddresses());
        }

        public IEnumerable<string> EnumerateAllowedIPAddresses()
        {
            return allowedIPAddresses.Select(b => b.ToIPAddress().ToString())
                .Union(firewall6.EnumerateAllowedIPAddresses());
        }

        public IEnumerable<IPAddressRange> EnumerateIPAddresses(string ruleNamePrefix = null)
        {
            string prefix = RulePrefix + (ruleNamePrefix ?? string.Empty);
            string[] pieces;

            foreach (string setFile in Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory, "*.set"))
            {
                if (Path.GetFileName(setFile).StartsWith(prefix))
                {
                    foreach (string line in File.ReadLines(setFile).Skip(1).Where(l => l.StartsWith("add ")))
                    {
                        // example line: add setname ipaddress -exist
                        pieces = line.Split(' ');
                        if (IPAddressRange.TryParse(pieces[2], out IPAddressRange range))
                        {
                            yield return range;
                        }
                    }
                }
            }

            foreach (IPAddressRange range in firewall6.EnumerateIPAddresses(ruleNamePrefix))
            {
                yield return range;
            }
        }

        public bool IsIPAddressBlocked(string ipAddress, int port = -1)
        {
            return bannedIPAddresses.Contains(IPBanFirewallUtility.ParseIPV4(ipAddress)) ||
                firewall6.IsIPAddressBlocked(ipAddress, port);
        }

        public bool IsIPAddressAllowed(string ipAddress)
        {
            return allowedIPAddresses.Contains(IPBanFirewallUtility.ParseIPV4(ipAddress)) ||
                firewall6.IsIPAddressAllowed(ipAddress);
        }

        public void Truncate()
        {
            bannedIPAddresses.Clear();
            allowedIPAddresses.Clear();
            RemoveAllTablesAndSets();
            firewall6.Truncate();
        }
    }
}

// https://linuxconfig.org/how-to-setup-ftp-server-on-ubuntu-18-04-bionic-beaver-with-vsftpd
// ipset create IPBanBlacklist iphash maxelem 1048576
// ipset destroy IPBanBlacklist // clear everything
// ipset -A IPBanBlacklist 10.10.10.10
// ipset -A IPBanBlacklist 10.10.10.11
// ipset save > file.txt
// ipset restore < file.txt
// iptables -A INPUT -m set --match-set IPBanBlacklist dst -j DROP
// iptables -F // clear all rules - this may break SSH permanently!
// iptables-save > file.txt
// iptables-restore < file.txt
// port ranges? iptables -A INPUT -p tcp -m tcp -m multiport --dports 1:79,81:442,444:65535 -j DROP
// list rules with line numbers: iptables -L --line-numbers
// modify rule at line number: iptables -R INPUT 12 -s 5.158.0.0/16 -j DROP
