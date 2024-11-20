﻿using Microsoft.Exchange.Data.Mime;
using Microsoft.Exchange.Data.Transport;
using Microsoft.Exchange.Data.Transport.Routing;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace ACSOnPremConnector
{
    /*
     * This class reroutes messages to external recipients when the header X-ACSOnPremConnector-Target set to a domain.
     * The domain value doesn't need to be routable, but has to be avalid domain (i.e. something.value.tld).
     * This agent will reroute all the messages via the custom routing domain, only if the recipient doens't exist in the GAL (Global Address List), meaning only valid recipients will be excluded from target domain override.
     * As the X-ACSOnPremConnector-Target will likely be set via Transport Rule, exclusions can still be managed via the transport rules themselves if necessary.
     * In case multiple agents are active at the same time, only the first one will trigger as the other will detect the presence of the X-ACSOnPremConnector-Target header which is used for loop protection. This is by design to protect mail loops.
     */
    public class RerouteExternalBasedOnGAL : RoutingAgentFactory
    {
        public override RoutingAgent CreateAgent(SmtpServer server)
        {
            return new ACSOnPremConnector_RerouteExternalBasedOnGAL(server.AddressBook);
        }
    }

    public class ACSOnPremConnector_RerouteExternalBasedOnGAL : RoutingAgent
    {
        EventLogger EventLog = new EventLogger("RerouteExternalBasedOnGAL");
        static readonly string ACSOnPremConnectorTargetName = "X-ACSOnPremConnector-Target";
        static string ACSOnPremConnectorTargetValue = String.Empty;

        static readonly string RegistryHive = @"Software\TransportAgents\ACSOnPremConnector\RerouteExternalBasedOnGAL";
        static readonly string RegistryKeyDebugEnabled = "DebugEnabled";
        static bool DebugEnabled = false;

        static readonly string ACSOnPremConnectorName = "X-ACSOnPremConnector-Name";
        static readonly string ACSOnPremConnectorNameValue = "ACSOnPremConnector-RerouteExternalBasedOnGAL";
        static readonly Dictionary<string, string> ACSOnPremConnectorHeaders = new Dictionary<string, string>
        {
            {ACSOnPremConnectorName, ACSOnPremConnectorNameValue},
            {"X-ACSOnPremConnector-Creator", "Tommaso Toniolo"},
            {"X-ACSOnPremConnector-Contact", "https://aka.ms/totoni"}
        };

        static AddressBook addressBook;

        public ACSOnPremConnector_RerouteExternalBasedOnGAL(AddressBook serverAddressBook)
        {
            base.OnResolvedMessage += new ResolvedMessageEventHandler(RerouteExternalBasedOnGAL);

            RegistryKey registryPath = Registry.CurrentUser.OpenSubKey(RegistryHive, RegistryKeyPermissionCheck.ReadWriteSubTree, System.Security.AccessControl.RegistryRights.FullControl);
            if (registryPath != null)
            {
                string registryKeyValue = null;
                bool valueConversionResult = false;

                registryKeyValue = registryPath.GetValue(RegistryKeyDebugEnabled, Boolean.FalseString).ToString();
                valueConversionResult = Boolean.TryParse(registryKeyValue, out DebugEnabled);
            }

            addressBook = serverAddressBook;

        }

        void RerouteExternalBasedOnGAL(ResolvedMessageEventSource source, QueuedMessageEventArgs evtMessage)
        {
            try
            {
                bool warningOccurred = false;
                string messageId = evtMessage.MailItem.Message.MessageId.ToString();
                string sender = evtMessage.MailItem.FromAddress.ToString().ToLower().Trim();
                string subject = evtMessage.MailItem.Message.Subject.Trim();
                HeaderList headers = evtMessage.MailItem.Message.MimeDocument.RootPart.Headers;
                Stopwatch stopwatch = Stopwatch.StartNew();

                EventLog.AppendLogEntry(String.Format("Processing message {0} from {1} with subject {2} in ACSOnPremConnector:RerouteExternalBasedOnGAL", messageId, sender, subject));

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
                            AddressBookEntry resolvedRecipient = addressBook.Find(recipient.Address);
                            EventLog.AppendLogEntry(String.Format("The check of whether the recipient {0} in the Address Book has returned a type of {1}", recipient.Address, resolvedRecipient == null ? "NULL" : resolvedRecipient.RecipientType.ToString()));

                            if (resolvedRecipient != null)
                            {
                                EventLog.AppendLogEntry(String.Format("Recipient {0} not overridden as IT EXISTS IN GAL", recipient.Address.ToString()));
                            }
                            else
                            {
                                RoutingDomain customRoutingDomain = new RoutingDomain(ACSOnPremConnectorTargetValue);
                                RoutingOverride destinationOverride = new RoutingOverride(customRoutingDomain, DeliveryQueueDomain.UseOverrideDomain);
                                source.SetRoutingOverride(recipient, destinationOverride);
                                EventLog.AppendLogEntry(String.Format("Recipient {0} overridden to {1}", recipient.Address.ToString(), ACSOnPremConnectorTargetValue));
                            }
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
                        Header HeaderExists = headers.FindFirst(newHeader.Key);
                        if (HeaderExists == null || HeaderExists.Value != newHeader.Value)
                        {
                            evtMessage.MailItem.Message.MimeDocument.RootPart.Headers.InsertAfter(new TextHeader(newHeader.Key, newHeader.Value), evtMessage.MailItem.Message.MimeDocument.RootPart.Headers.LastChild);
                            EventLog.AppendLogEntry(String.Format("ADDED header {0}: {1}", newHeader.Key, String.IsNullOrEmpty(newHeader.Value) ? String.Empty : newHeader.Value));
                        }
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

                EventLog.AppendLogEntry(String.Format("ACSOnPremConnector:RerouteExternalBasedOnGAL took {0} ms to execute", stopwatch.ElapsedMilliseconds));

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
                EventLog.AppendLogEntry("Exception in ACSOnPremConnector:RerouteExternalBasedOnGAL");
                EventLog.AppendLogEntry(ex);
                EventLog.LogError();
            }

            return;

        }
    }
}