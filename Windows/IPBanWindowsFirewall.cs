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

#region Imports

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using NetFwTypeLib;

#endregion Imports

namespace DigitalRuby.IPBan
{
    // TODO: Use https://github.com/falahati/NetworkAdapterSelector/blob/master/NetworkAdapterSelector.Hook/Guest.cs
    // https://falahati.net/my-blog/103-bind-ip-in-c-sharp-code-injection-shell-extension

    /// <summary>
    /// Helper class for Windows firewall and banning ip addresses.
    /// </summary>
    [RequiredOperatingSystem(IPBanOS.Windows)]
    [CustomName("Default")]
    public class IPBanWindowsFirewall : IPBanBaseFirewall, IIPBanFirewall
    {
        // DO NOT CHANGE THESE CONST AND READONLY FIELDS!

        /// <summary>
        /// Max number of ip addresses per rule
        /// </summary>
        public const int MaxIpAddressesPerRule = 1000;

        private const string clsidFwPolicy2 = "{E2B3C97F-6AE1-41AC-817A-F6F92166D7DD}";
        private const string clsidFwRule = "{2C5BC43E-3369-4C33-AB0C-BE9469677AF4}";
        private static readonly INetFwPolicy2 policy = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid(clsidFwPolicy2))) as INetFwPolicy2;
        private static readonly Type ruleType = Type.GetTypeFromCLSID(new Guid(clsidFwRule));
        private static readonly char[] firewallEntryDelimiters = new char[] { '/', '-' };

        private string CreateRuleStringForIPAddresses(IReadOnlyList<string> ipAddresses, int index, int count)
        {
            if (count == 0 || index >= ipAddresses.Count)
            {
                return string.Empty;
            }

            // don't overrun array
            count = Math.Min(count, ipAddresses.Count - index);

            StringBuilder b = new StringBuilder(count * 16);
            foreach (string ipAddress in ipAddresses.Skip(index).Take(count))
            {
                if (ipAddress.TryGetFirewallIPAddress(out string firewallIPAddress))
                {
                    b.Append(firewallIPAddress);
                    b.Append(',');
                }
            }
            if (b.Length != 0)
            {
                // remove ending comma
                b.Length--;
            }

            return b.ToString();
        }

        private bool GetOrCreateRule(string ruleName, string remoteIPAddresses, NET_FW_ACTION_ action, IEnumerable<PortRange> allowedPorts = null)
        {
            remoteIPAddresses = (remoteIPAddresses ?? string.Empty).Trim();
            bool emptyIPAddressString = string.IsNullOrWhiteSpace(remoteIPAddresses) || remoteIPAddresses == "*";
            bool ruleNeedsToBeAdded = false;

            lock (policy)
            {
recreateRule:
                INetFwRule rule = null;
                try
                {
                    rule = policy.Rules.Item(ruleName);
                }
                catch
                {
                    // ignore exception, assume does not exist
                }
                if (rule == null)
                {
                    rule = Activator.CreateInstance(ruleType) as INetFwRule;
                    rule.Name = ruleName;
                    rule.Enabled = true;
                    rule.Action = action;
                    rule.Description = "Automatically created by IPBan";
                    rule.Direction = NET_FW_RULE_DIRECTION_.NET_FW_RULE_DIR_IN;
                    rule.EdgeTraversal = false;
                    rule.Grouping = "IPBan";
                    rule.LocalAddresses = "*";
                    rule.Profiles = int.MaxValue; // all
                    ruleNeedsToBeAdded = true;
                }

                // do not ever set an empty string, Windows treats this as * which means everything
                if (!emptyIPAddressString)
                {
                    try
                    {
                        PortRange[] allowedPortsArray = (allowedPorts?.ToArray());
                        if (allowedPortsArray != null && allowedPortsArray.Length != 0)
                        {
                            rule.Protocol = (int)NET_FW_IP_PROTOCOL_.NET_FW_IP_PROTOCOL_TCP;
                            string localPorts;
                            if (action == NET_FW_ACTION_.NET_FW_ACTION_BLOCK)
                            {
                                localPorts = IPBanFirewallUtility.GetPortRangeStringBlockExcept(allowedPortsArray);
                            }
                            else
                            {
                                localPorts = IPBanFirewallUtility.GetPortRangeStringAllow(allowedPortsArray);
                            }
                            rule.LocalPorts = localPorts;
                        }
                        else
                        {
                            try
                            {
                                rule.Protocol = (int)NET_FW_IP_PROTOCOL_.NET_FW_IP_PROTOCOL_ANY;
                            }
                            catch
                            {
                                // failed to set protocol to any, we are switching from tcp back to any without ports, the only option is to
                                //  recreate the rule
                                if (!ruleNeedsToBeAdded)
                                {
                                    policy.Rules.Remove(ruleName);
                                    goto recreateRule;
                                }
                            }
                        }
                        rule.RemoteAddresses = remoteIPAddresses;
                    }
                    catch (Exception ex)
                    {
                        // if something failed, do not create the rule
                        emptyIPAddressString = true;
                        IPBanLog.Error(ex);
                    }
                }

                if (emptyIPAddressString || string.IsNullOrWhiteSpace(rule.RemoteAddresses) || rule.RemoteAddresses == "*")
                {
                    // if no ip addresses, remove the rule as it will allow or block everything with an empty RemoteAddresses string
                    try
                    {
                        rule = null;
                        policy.Rules.Remove(ruleName);
                    }
                    catch
                    {
                    }
                }
                else if (ruleNeedsToBeAdded)
                {
                    policy.Rules.Add(rule);
                }
                return (rule != null);
            }
        }

        private void CreateBlockRule(IReadOnlyList<string> ipAddresses, int index, int count, string ruleName)
        {
            string remoteIpString = CreateRuleStringForIPAddresses(ipAddresses, index, count);
            GetOrCreateRule(ruleName, remoteIpString, NET_FW_ACTION_.NET_FW_ACTION_BLOCK);
        }

        private void MigrateOldDefaultRuleNames()
        {
            // migrate old default rule names to new names
            INetFwRule rule = null;
            for (int i = 0; ; i += MaxIpAddressesPerRule)
            {
                lock (policy)
                {
                    try
                    {
                        rule = null;
                        try
                        {
                            // migrate really old style
                            rule = policy.Rules.Item("IPBan_BlockIPAddresses_" + i.ToString(CultureInfo.InvariantCulture));
                        }
                        catch
                        {
                            // not exist, that is OK
                        }
                        if (rule == null)
                        {
                            // migrate IPBan_0 style to IPBan_Block_0 style
                            rule = policy.Rules.Item("IPBan_" + i.ToString(CultureInfo.InvariantCulture));
                        }
                        rule.Name = BlockRulePrefix + i.ToString(CultureInfo.InvariantCulture);
                    }
                    catch
                    {
                        // ignore exception, assume does not exist
                        break;
                    }
                }
            }
            lock (policy)
            {
                try
                {
                    rule = policy.Rules.Item("IPBan_BlockIPAddresses_AllowIPAddresses");
                    rule.Name = AllowRulePrefix + "0";
                }
                catch
                {
                    // ignore exception, assume does not exist
                }
            }
        }

        private bool DeleteRules(string ruleNamePrefix, int startIndex = 0)
        {
            try
            {
                lock (policy)
                {
                    foreach (INetFwRule rule in EnumerateRulesMatchingPrefix(ruleNamePrefix).ToArray())
                    {
                        try
                        {
                            Match match = Regex.Match(rule.Name, $"^{ruleNamePrefix}(?<num>[0-9]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                            if (match.Success && int.TryParse(match.Groups["num"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int num) && num >= startIndex)
                            {
                                policy.Rules.Remove(rule.Name);
                            }
                        }
                        catch
                        {
                        }
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                IPBanLog.Error(ex);
                return false;
            }
        }

        private IEnumerable<INetFwRule> EnumerateRulesMatchingPrefix(string ruleNamePrefix)
        {
            // powershell example
            // (New-Object -ComObject HNetCfg.FwPolicy2).rules | Where-Object { $_.Name -match '^prefix' } | ForEach-Object { Write-Output "$($_.Name)" }
            // TODO: Revisit COM interface in .NET core 3.0
            var e = policy.Rules.GetEnumeratorVariant();
            object[] results = new object[64];
            int count;
            IntPtr bufferLengthPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(int)));
            try
            {
                do
                {
                    e.Next(results.Length, results, bufferLengthPointer);
                    count = Marshal.ReadInt32(bufferLengthPointer);
                    foreach (object o in results)
                    {
                        if ((o is INetFwRule rule) &&
                            (ruleNamePrefix == null || ruleNamePrefix == "*" || rule.Name.StartsWith(ruleNamePrefix, StringComparison.OrdinalIgnoreCase)))
                        {
                            yield return rule;
                        }
                    }
                }
                while (count == results.Length);
            }
            finally
            {
                Marshal.FreeCoTaskMem(bufferLengthPointer);
            }

            /*
            System.Diagnostics.Process p = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    Arguments = "advfirewall firewall show rule name=all",
                    CreateNoWindow = true,
                    FileName = "netsh.exe",
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                }
            };
            p.Start();
            string line;
            string ruleName;
            INetFwRule rule;
            Regex regex = new Regex(": +" + prefix + ".*");
            Match match;

            while ((line = p.StandardOutput.ReadLine()) != null)
            {
                match = regex.Match(line);
                if (match.Success)
                {
                    ruleName = match.Value.Trim(' ', ':');
                    rule = null;
                    try
                    {
                        rule = policy.Rules.Item(ruleName);
                    }
                    catch
                    {
                    }
                    if (rule != null)
                    {
                        yield return rule;
                    }
                }
            }
            */
        }

        public IPBanWindowsFirewall(string rulePrefix = null) : base(rulePrefix)
        {
            MigrateOldDefaultRuleNames();
        }

        public Task<bool> BlockIPAddresses(string ruleNamePrefix, IEnumerable<string> ipAddresses, CancellationToken cancelToken = default)
        {
            try
            {
                string prefix = (string.IsNullOrWhiteSpace(ruleNamePrefix) ? BlockRulePrefix : RulePrefix + ruleNamePrefix).TrimEnd('_') + "_";
                int i = 0;
                List<string> ipAddressesList = new List<string>();
                foreach (string ipAddress in ipAddresses)
                {
                    if (cancelToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(cancelToken);
                    }
                    ipAddressesList.Add(ipAddress);
                    if (ipAddressesList.Count == MaxIpAddressesPerRule)
                    {
                        CreateBlockRule(ipAddressesList, 0, MaxIpAddressesPerRule, prefix + i.ToStringInvariant());
                        i += MaxIpAddressesPerRule;
                        ipAddressesList.Clear();
                    }
                }
                if (cancelToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancelToken);
                }
                if (ipAddressesList.Count != 0)
                {
                    CreateBlockRule(ipAddressesList, 0, MaxIpAddressesPerRule, prefix + i.ToStringInvariant());
                    i += MaxIpAddressesPerRule;
                }
                DeleteRules(prefix, i);
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                IPBanLog.Error(ex);
                return Task.FromResult(false);
            }
        }

        public Task<bool> BlockIPAddressesDelta(string ruleNamePrefix, IEnumerable<IPBanFirewallIPAddressDelta> ipAddresses, CancellationToken cancelToken = default)
        {
            string prefix = (string.IsNullOrWhiteSpace(ruleNamePrefix) ? BlockRulePrefix : RulePrefix + ruleNamePrefix).TrimEnd('_') + "_";
            int ruleIndex;
            INetFwRule[] rules = EnumerateRulesMatchingPrefix(prefix).ToArray();
            List<HashSet<string>> remoteIPAddresses = new List<HashSet<string>>();
            List<bool> ruleChanges = new List<bool>();
            for (int i = 0; i < rules.Length; i++)
            {
                string[] ipList = rules[i].RemoteAddresses.Split(',');
                HashSet<string> ipSet = new HashSet<string>();
                foreach (string ip in ipList)
                {
                    // trim out submask
                    int pos = ip.IndexOf('/');
                    if (pos >= 0)
                    {
                        ipSet.Add(ip.Substring(0, pos));
                    }
                    else
                    {
                        ipSet.Add(ip);
                    }
                }
                remoteIPAddresses.Add(ipSet);
                ruleChanges.Add(false);
            }
            List<IPBanFirewallIPAddressDelta> deltas = new List<IPBanFirewallIPAddressDelta>(ipAddresses);
            deltas.Sort((d1, d2) => d2.Added.CompareTo(d1.Added));
            for (int deltaIndex = deltas.Count - 1; deltaIndex >= 0; deltaIndex--)
            {
                for (ruleIndex = 0; ruleIndex < remoteIPAddresses.Count; ruleIndex++)
                {
                    HashSet<string> remoteIPAddressesSet = remoteIPAddresses[ruleIndex];
                    bool change = false;
                    if (deltas[deltaIndex].Added)
                    {
                        if (remoteIPAddressesSet.Count < MaxIpAddressesPerRule)
                        {
                            change = remoteIPAddressesSet.Add(deltas[deltaIndex].IPAddress);
                        }
                    }
                    else
                    {
                        change = remoteIPAddressesSet.Remove(deltas[deltaIndex].IPAddress);
                    }
                    if (change)
                    {
                        deltas.RemoveAt(deltaIndex);
                        ruleChanges[ruleIndex] = true;
                        break;
                    }
                }
            }

            // any remaining deltas for adding need to go in new rules if they did not fit in the existing rules
            string[] remainingIPAddresses = deltas.Where(d => d.Added).Select(d => d.IPAddress).ToArray();
            for (int i = 0; i < remainingIPAddresses.Length; i += MaxIpAddressesPerRule)
            {
                remoteIPAddresses.Add(new HashSet<string>(remainingIPAddresses.Skip(i).Take(MaxIpAddressesPerRule).Where(i2 => IPAddress.TryParse(i2, out _))));
                ruleChanges.Add(true);
            }

            // update the rules
            ruleIndex = 0;
            for (int i = 0; i < remoteIPAddresses.Count; i++)
            {
                if (ruleChanges[i])
                {
                    string name = (i < rules.Length ? rules[i].Name : prefix + ruleIndex.ToStringInvariant());
                    GetOrCreateRule(name, string.Join(',', remoteIPAddresses[i]), NET_FW_ACTION_.NET_FW_ACTION_BLOCK);
                }
                ruleIndex += MaxIpAddressesPerRule;
            }

            return Task.FromResult(true);
        }

        public Task<bool> BlockIPAddresses(string ruleNamePrefix, IEnumerable<IPAddressRange> ranges, IEnumerable<PortRange> allowedPorts, CancellationToken cancelToken = default)
        {
            try
            {
                string prefix = (string.IsNullOrWhiteSpace(ruleNamePrefix) ? RangeRulePrefix : RulePrefix + ruleNamePrefix).TrimEnd('_') + "_";

                // recreate rules
                int counter = 0;
                int index = 0;
                StringBuilder ipList = new StringBuilder();
                foreach (IPAddressRange range in ranges)
                {
                    if (cancelToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(cancelToken);
                    }
                    ipList.Append(range.ToCidrString());
                    ipList.Append(',');
                    if (++counter == MaxIpAddressesPerRule)
                    {
                        ipList.Length--; // remove ending comma
                        GetOrCreateRule(prefix + index.ToString(CultureInfo.InvariantCulture), ipList.ToString(), NET_FW_ACTION_.NET_FW_ACTION_BLOCK, allowedPorts);
                        counter = 0;
                        index += MaxIpAddressesPerRule;
                        ipList.Clear();
                    }
                }
                if (cancelToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancelToken);
                }

                // create rule for any leftover ip addresses
                if (ipList.Length > 1)
                {
                    ipList.Length--; // remove ending comma
                    GetOrCreateRule(prefix + index.ToString(CultureInfo.InvariantCulture), ipList.ToString(), NET_FW_ACTION_.NET_FW_ACTION_BLOCK, allowedPorts);
                    index += MaxIpAddressesPerRule;
                }

                // delete any leftover rules
                DeleteRules(prefix, index);
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                IPBanLog.Error(ex);
                return Task.FromResult(false);
            }
        }

        public Task<bool> AllowIPAddresses(IEnumerable<string> ipAddresses, CancellationToken cancelToken = default)
        {
            try
            {
                List<string> ipAddressesList = new List<string>();
                int i = 0;
                foreach (string ipAddress in ipAddresses)
                {
                    if (cancelToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException();
                    }
                    ipAddressesList.Add(ipAddress);
                    if (ipAddressesList.Count == MaxIpAddressesPerRule)
                    {
                        string remoteIP = CreateRuleStringForIPAddresses(ipAddressesList, i, MaxIpAddressesPerRule);
                        GetOrCreateRule(AllowRulePrefix + i.ToString(CultureInfo.InvariantCulture), remoteIP, NET_FW_ACTION_.NET_FW_ACTION_ALLOW);
                        i += MaxIpAddressesPerRule;
                        ipAddressesList.Clear();
                    }
                }
                if (cancelToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException();
                }
                if (ipAddressesList.Count != 0)
                {
                    string remoteIP = CreateRuleStringForIPAddresses(ipAddressesList, i, MaxIpAddressesPerRule);
                    GetOrCreateRule(AllowRulePrefix + i.ToString(CultureInfo.InvariantCulture), remoteIP, NET_FW_ACTION_.NET_FW_ACTION_ALLOW);
                    i += MaxIpAddressesPerRule;
                }
                if (cancelToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException();
                }
                DeleteRules(AllowRulePrefix, i);
                return Task.FromResult<bool>(true);
            }
            catch (Exception ex)
            {
                IPBanLog.Error(ex);
                return Task.FromResult<bool>(false);
            }
        }

        public bool IsIPAddressBlocked(string ipAddress, int port = -1)
        {
            try
            {
                lock (policy)
                {
                    for (int i = 0; ; i += MaxIpAddressesPerRule)
                    {
                        string ruleName = BlockRulePrefix + i.ToString(CultureInfo.InvariantCulture);
                        try
                        {
                            INetFwRule rule = policy.Rules.Item(ruleName);
                            if (rule == null)
                            {
                                // no more rules to check
                                break;
                            }
                            else
                            {
                                HashSet<string> set = new HashSet<string>(rule.RemoteAddresses.Split(',').Select(i2 => IPAddressRange.Parse(i2).Begin.ToString()));
                                if (set.Contains(ipAddress))
                                {
                                    return true;
                                }
                            }
                        }
                        catch
                        {
                            // no more rules to check
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                IPBanLog.Error(ex);
            }
            return false;
        }

        public bool IsIPAddressAllowed(string ipAddress)
        {
            try
            {
                lock (policy)
                {
                    for (int i = 0; ; i += MaxIpAddressesPerRule)
                    {
                        string ruleName = AllowRulePrefix + i.ToString(CultureInfo.InvariantCulture);
                        try
                        {
                            INetFwRule rule = policy.Rules.Item(ruleName);
                            if (rule == null)
                            {
                                break;
                            }
                            else if (rule.RemoteAddresses.Contains(ipAddress))
                            {
                                return true;
                            }
                        }
                        catch
                        {
                            // OK, rule does not exist
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                IPBanLog.Error(ex);
            }
            return false;
        }

        public IEnumerable<string> GetRuleNames(string ruleNamePrefix = null)
        {
            string prefix = (string.IsNullOrWhiteSpace(ruleNamePrefix) ? RulePrefix : RulePrefix + ruleNamePrefix);
            return EnumerateRulesMatchingPrefix(prefix).OrderBy(r => r.Name).Select(r => r.Name);
        }

        public bool RuleExists(string ruleName)
        {
            try
            {
                return (policy.Rules.Item(ruleName) != null);
            }
            catch
            {
            }
            return false;
        }

        public bool DeleteRule(string ruleName)
        {
            try
            {
                INetFwRule rule = policy.Rules.Item(ruleName);
                policy.Rules.Remove(rule.Name);
                return true;
            }
            catch
            {
            }
            return false;
        }

        public IEnumerable<string> EnumerateBannedIPAddresses()
        {
            int i = 0;
            INetFwRule rule;

            while (true)
            {
                string ruleName = BlockRulePrefix + i.ToString(CultureInfo.InvariantCulture);
                try
                {
                    rule = policy.Rules.Item(ruleName);
                    if (rule == null)
                    {
                        break;
                    }
                }
                catch
                {
                    // does not exist
                    break;
                }
                foreach (string ip in rule.RemoteAddresses.Split(','))
                {
                    int pos = ip.IndexOfAny(firewallEntryDelimiters);
                    if (pos < 0)
                    {
                        yield return ip;
                    }
                    else
                    {
                        yield return ip.Substring(0, pos);
                    }
                }
                i += MaxIpAddressesPerRule;
            }
        }

        public IEnumerable<string> EnumerateAllowedIPAddresses()
        {
            INetFwRule rule;
            for (int i = 0; ; i += MaxIpAddressesPerRule)
            {
                try
                {
                    rule = policy.Rules.Item(AllowRulePrefix + i.ToString(CultureInfo.InvariantCulture));
                    if (rule == null)
                    {
                        break;
                    }
                }
                catch
                {
                    // OK, rule does not exist
                    yield break;
                }
                foreach (string ip in rule.RemoteAddresses.Split(','))
                {
                    int pos = ip.IndexOf('/');
                    if (pos < 0)
                    {
                        yield return ip;
                    }
                    else
                    {
                        yield return ip.Substring(0, pos);
                    }
                }
            }
        }

        public IEnumerable<IPAddressRange> EnumerateIPAddresses(string ruleNamePrefix = null)
        {
            string prefix = (string.IsNullOrWhiteSpace(ruleNamePrefix) ? RulePrefix : RulePrefix + ruleNamePrefix);
            foreach (INetFwRule rule in EnumerateRulesMatchingPrefix(prefix))
            {
                string ipList = rule.RemoteAddresses;
                if (!string.IsNullOrWhiteSpace(ipList) && ipList != "*")
                {
                    string[] ips = ipList.Split(',');
                    foreach (string ip in ips)
                    {
                        if (IPAddressRange.TryParse(ip, out IPAddressRange range))
                        {
                            yield return range;
                        }
                        // else // should never happen
                    }
                }
            }
        }

        public void EnableLocalSubnetTrafficViaFirewall()
        {
            string ruleName = RulePrefix + "AllowLocalTraffic";
            string localIP = DefaultDnsLookup.GetLocalIPAddress().ToString();
            if (localIP != null)
            {
                Match m = Regex.Match(localIP, "\\.[0-9]+$");
                if (m.Success)
                {
                    string remoteIPAddresses = localIP.Substring(0, m.Index) + ".0/24";
                    GetOrCreateRule(ruleName, remoteIPAddresses, NET_FW_ACTION_.NET_FW_ACTION_ALLOW);
                }
            }
        }

        public Task UnblockIPAddresses(IEnumerable<string> ipAddresses)
        {
            try
            {
                lock (policy)
                {
                    foreach (INetFwRule rule in EnumerateRulesMatchingPrefix(RulePrefix).ToArray())
                    {
                        if (rule.Name.StartsWith(AllowRulePrefix))
                        {
                            continue;
                        }

                        string remoteIPs = rule.RemoteAddresses;
                        foreach (string ipAddress in ipAddresses)
                        {
                            remoteIPs = Regex.Replace(remoteIPs, ipAddress.Replace(".", "\\.") + "\\/[^,]+,?", ",", RegexOptions.IgnoreCase);
                            remoteIPs = remoteIPs.Replace(",,", ",");
                            remoteIPs = remoteIPs.Trim().Trim(',', '/', ':', '.', ';', '*').Trim();
                        }
                        if (remoteIPs != rule.RemoteAddresses)
                        {
                            // ensure we don't have a block rule with no ip addresses, this will block the entire world (WTF Microsoft)...
                            if (string.IsNullOrWhiteSpace(remoteIPs))
                            {
                                policy.Rules.Remove(rule.Name);
                            }
                            else
                            {
                                rule.RemoteAddresses = remoteIPs;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                IPBanLog.Error(ex);
            }
            return Task.CompletedTask;
        }

        public void Truncate()
        {
            foreach (INetFwRule rule in EnumerateRulesMatchingPrefix(RulePrefix).ToArray())
            {
                lock (policy)
                {
                    try
                    {
                        policy.Rules.Remove(rule.Name);
                    }
                    catch
                    {
                    }
                }
            }
        }
    }
}
