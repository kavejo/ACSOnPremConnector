﻿using Microsoft.Exchange.Data.Mime;
using Microsoft.Exchange.Data.Transport;
using Microsoft.Exchange.Data.Transport.Routing;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

namespace ACSOnPremConnector
{
    public class RerouteAll : RoutingAgentFactory
    {
        public override RoutingAgent CreateAgent(SmtpServer server)
        {
            return new ACSOnPremConnector_RerouteAll(server.AcceptedDomains, server.AddressBook);
        }
}

    public class ACSOnPremConnector_RerouteAll : RoutingAgent
    {
        EventLogger EventLog = new EventLogger("ACSOnPremConnector");
        static readonly string ACSOnPremConnectorTargetName = "X-ACSOnPremConnector-Target";
        static string ACSOnPremConnectorTargetValue = String.Empty;

        static readonly string RegistryHive = @"Software\TransportAgents\ACSOnPremConnector\RerouteAll";
        static readonly string RegistryKeyDebugEnabled = "DebugEnabled";
        static bool DebugEnabled = false;

        static readonly string ACSOnPremConnectorName = "X-TransportAgent-Name";
        static readonly string ACSOnPremConnectorNameValue = "ACSOnPremConnector-RerouteAll";
        static readonly Dictionary<string, string> ACSOnPremConnectorHeaders = new Dictionary<string, string>
        {
            {ACSOnPremConnectorName, ACSOnPremConnectorNameValue},
            {"X-TransportAgent-Creator", "Tommaso Toniolo"},
            {"X-TransportAgent-Contact", "https://aka.ms/totoni"}
        };

        static AcceptedDomainCollection acceptedDomains;
        static AddressBook addressBook;

        public ACSOnPremConnector_RerouteAll(AcceptedDomainCollection serverAcceptedDomains, AddressBook serverAddressBook)
        {
            base.OnResolvedMessage += new ResolvedMessageEventHandler(OverrideRoutingDomain);

            RegistryKey registryPath = Registry.CurrentUser.OpenSubKey(RegistryHive, RegistryKeyPermissionCheck.ReadWriteSubTree, System.Security.AccessControl.RegistryRights.FullControl);
            if (registryPath != null)
            {
                string registryKeyValue = null;
                bool valueConversionResult = false;

                registryKeyValue = registryPath.GetValue(RegistryKeyDebugEnabled, Boolean.FalseString).ToString();
                valueConversionResult = Boolean.TryParse(registryKeyValue, out DebugEnabled);
            }

            acceptedDomains = serverAcceptedDomains;
            addressBook = serverAddressBook;

        }

        void OverrideRoutingDomain(ResolvedMessageEventSource source, QueuedMessageEventArgs evtMessage)
        {
            try
            {
                bool warningOccurred = false;
                string messageId = evtMessage.MailItem.Message.MessageId.ToString();
                string sender = evtMessage.MailItem.FromAddress.ToString().ToLower().Trim();
                string subject = evtMessage.MailItem.Message.Subject.Trim();
                HeaderList headers = evtMessage.MailItem.Message.MimeDocument.RootPart.Headers;
                Stopwatch stopwatch = Stopwatch.StartNew();

                EventLog.AppendLogEntry(String.Format("Processing message {0} from {1} with subject {2} in ACSOnPremConnector:RerouteAll", messageId, sender, subject));

                Header ACSOnPremConnectorTarget = headers.FindFirst(ACSOnPremConnectorTargetName);
                Header LoopPreventionHeader = headers.FindFirst(ACSOnPremConnectorName);

                if (ACSOnPremConnectorTarget != null && evtMessage.MailItem.Message.IsSystemMessage == false && LoopPreventionHeader == null)
                {
                    EventLog.AppendLogEntry(String.Format("Rerouting messages as the control header {0} is present", ACSOnPremConnectorTargetName));
                    ACSOnPremConnectorTargetValue = ACSOnPremConnectorTarget.Value.Trim();

                    if (!String.IsNullOrEmpty(ACSOnPremConnectorTargetValue) && (Uri.CheckHostName(ACSOnPremConnectorTargetValue) == UriHostNameType.Dns))
                    {
                        EventLog.AppendLogEntry(String.Format("Rerouting domain is valid as the header {0} is set to {1}", ACSOnPremConnectorTargetName, ACSOnPremConnectorTargetValue));

                        foreach (EnvelopeRecipient recipient in evtMessage.MailItem.Recipients)
                        {
                            RoutingDomain customRoutingDomain = new RoutingDomain(ACSOnPremConnectorTargetValue);
                            RoutingOverride destinationOverride = new RoutingOverride(customRoutingDomain, DeliveryQueueDomain.UseOverrideDomain);
                            source.SetRoutingOverride(recipient, destinationOverride);
                            EventLog.AppendLogEntry(String.Format("Recipient {0} overridden to {1}", recipient.Address.ToString(), ACSOnPremConnectorTargetValue));
                        }
                    }
                    else
                    {
                        EventLog.AppendLogEntry(String.Format("There was a problem processing the {0} header value", ACSOnPremConnectorTargetName));
                        EventLog.AppendLogEntry(String.Format("There value retrieved is: {0}", ACSOnPremConnectorTargetValue));
                        warningOccurred = true;
                    }

                    foreach (var newHeader in ACSOnPremConnectorHeaders)
                    {
                        evtMessage.MailItem.Message.MimeDocument.RootPart.Headers.InsertAfter(new TextHeader(newHeader.Key, newHeader.Value), evtMessage.MailItem.Message.MimeDocument.RootPart.Headers.LastChild);
                        EventLog.AppendLogEntry(String.Format("ADDED header {0}: {1}", newHeader.Key, String.IsNullOrEmpty(newHeader.Value) ? String.Empty : newHeader.Value));
                    }

                }
                else
                {
                    if (evtMessage.MailItem.Message.IsSystemMessage == true)
                    {
                        EventLog.AppendLogEntry(String.Format("Message has not been processed as IsSystemMessage"));
                    }
                    else if (LoopPreventionHeader != null)
                    {
                        EventLog.AppendLogEntry(String.Format("Message has not been processed as {0} is already present", LoopPreventionHeader.Name));
                        EventLog.AppendLogEntry(String.Format("This might mean there is a mail LOOP. Trace the message carefully."));
                        warningOccurred = true;
                    }
                    else
                    {
                        EventLog.AppendLogEntry(String.Format("Message has not been processed as {0} is not set", ACSOnPremConnectorTargetName));
                    }
                }

                EventLog.AppendLogEntry(String.Format("ACSOnPremConnector:RerouteAll took {0} ms to execute", stopwatch.ElapsedMilliseconds));

                if (warningOccurred)
                {
                    EventLog.LogWarning();
                }
                else
                {
                    EventLog.LogDebug(DebugEnabled);
                }

            }
            catch (Exception ex)
            {
                EventLog.AppendLogEntry("Exception in ACSOnPremConnector:RerouteAll");
                EventLog.AppendLogEntry(ex);
                EventLog.LogError();
            }

            return;

        }
    }
}