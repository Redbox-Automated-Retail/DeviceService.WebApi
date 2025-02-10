using DeviceService.ComponentModel;
using DeviceService.ComponentModel.Analytics;
using DeviceService.ComponentModel.Commands;
using DeviceService.ComponentModel.Requests;
using DeviceService.ComponentModel.Responses;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DeviceService.WebApi.Services
{
    public class CardReaderHub : Hub
    {
        private IIUC285Proxy _proxy;
        private ILogger<CardReaderHub> _logger;
        private IActivationService _activationService;
        private static object _syncObject = new object();
        private IDeviceStatusService _deviceStatusService;
        private IApplicationControl _applicationControl;
        private IAnalyticsService _analytics;
        private static ConcurrentDictionary<Guid, CancellationTokenSource> _cancellationTokenSources = new ConcurrentDictionary<Guid, CancellationTokenSource>();
        private static int _configureOccurred = 0;
        private static CardReaderHub.QueuedCommand ActiveCommand = (CardReaderHub.QueuedCommand)null;
        private static Task _processCommandQueue;
        private static Dictionary<string, Action<CardReaderHub.QueuedCommand>> _commandProcessors;
        private static ConcurrentQueue<CardReaderHub.QueuedCommand> _commandQueue = new ConcurrentQueue<CardReaderHub.QueuedCommand>();
        private static bool _commandQueuePaused = false;

        public CardReaderHub(
          IIUC285Proxy proxy,
          ILogger<CardReaderHub> logger,
          IActivationService activationService,
          IDeviceStatusService deviceStatusService,
          IApplicationControl applicationControl,
          IAnalyticsService analytics)
        {
            this._proxy = proxy;
            this._logger = logger;
            this._activationService = activationService;
            this._deviceStatusService = deviceStatusService;
            this._applicationControl = applicationControl;
            this._analytics = analytics;
            this.ConfigureCommandProcessors();
        }

        public void SendMessage(string message)
        {
            ClientProxyExtensions.SendAsync(((IHubClients<IClientProxy>)this.Clients).All, "ReceiveMessage", (object)"username", (object)message, new CancellationToken());
        }

        public void SendSimpleEvent(SimpleEvent simpleEvent)
        {
            if (simpleEvent == null)
                return;
            switch (simpleEvent.EventName)
            {
                case "CardReaderConnectedEvent":
                    ClientProxyExtensions.SendAsync(((IHubClients<IClientProxy>)this.Clients).All, "CardReaderConnectedEvent", (object)simpleEvent, new CancellationToken());
                    break;
                case "CardReaderDisconnectedEvent":
                    ClientProxyExtensions.SendAsync(((IHubClients<IClientProxy>)this.Clients).All, "CardReaderDisconnectedEvent", (object)simpleEvent, new CancellationToken());
                    break;
                case "CardRemovedResponseEvent":
                    ClientProxyExtensions.SendAsync(((IHubClients<IClientProxy>)this.Clients).All, "CardRemovedResponseEvent", (object)simpleEvent, new CancellationToken());
                    break;
                case "DeviceServiceCanShutDownEvent":
                    ClientProxyExtensions.SendAsync(((IHubClients<IClientProxy>)this.Clients).All, "DeviceServiceCanShutDownEvent", (object)simpleEvent, new CancellationToken());
                    break;
                default:
                    ClientProxyExtensions.SendAsync(((IHubClients<IClientProxy>)this.Clients).All, simpleEvent.EventName, (object)simpleEvent, new CancellationToken());
                    break;
            }
        }

        public static bool IsProcessingCommands
        {
            get
            {
                return CardReaderHub._processCommandQueue != null && !CardReaderHub._processCommandQueue.IsCompleted;
            }
        }

        private bool CommandQueuePaused
        {
            get => CardReaderHub._commandQueuePaused;
            set
            {
                if (value == CardReaderHub._commandQueuePaused)
                    return;
                CardReaderHub._commandQueuePaused = value;
                string str = CardReaderHub._commandQueuePaused ? "paused" : "restarted";
                ILogger<CardReaderHub> logger = this._logger;
                if (logger != null)
                    logger.LogInformation("Command Queue " + str);
                if (CardReaderHub._commandQueuePaused)
                    return;
                this.StartProcessingQueuedCommands();
            }
        }

        private void AddQueuedCommand(CardReaderHub.QueuedCommand queuedCommand)
        {
            if (queuedCommand?.Command == null)
                return;
            queuedCommand.ConnectionId = this.Context.ConnectionId;
            lock (CardReaderHub._syncObject)
            {
                if (queuedCommand.Command.IsQueuedCommand && queuedCommand.Command.CommandName != "IsConnected" && queuedCommand.Command.CommandName != "SupportsEMV")
                {
                    CardReaderHub._commandQueue.Enqueue(queuedCommand);
                }
                else
                {
                    Action<CardReaderHub.QueuedCommand> action;
                    if (CardReaderHub._commandProcessors.TryGetValue(queuedCommand.Command.CommandName, out action))
                    {
                        if (action != null)
                            action(queuedCommand);
                    }
                }
            }
            ILogger<CardReaderHub> logger = this._logger;
            if (logger != null)
                logger.LogInformation(string.Format("Added command {0} to Queued commands with request id: {1} with {2} commands in queue.", (object)queuedCommand.Command.CommandName, (object)queuedCommand.Command.RequestId, (object)CardReaderHub._commandQueue.Count));
            this.StartProcessingQueuedCommands();
        }

        private void StartProcessingQueuedCommands()
        {
            if (CardReaderHub._processCommandQueue == null || CardReaderHub._processCommandQueue.IsCompleted)
            {
                CardReaderHub._processCommandQueue = Task.Run((Action)(() => this.ProcessQueuedCommands()));
            }
            else
            {
                ILogger<CardReaderHub> logger = this._logger;
                if (logger == null)
                    return;
                logger.LogInformation("ProcessQueuedCommands task already running");
            }
        }

        private void ConfigureCommandProcessors()
        {
            if (CardReaderHub._commandProcessors != null || Interlocked.CompareExchange(ref CardReaderHub._configureOccurred, 1, 0) != 0)
                return;
            CardReaderHub._commandProcessors = new Dictionary<string, Action<CardReaderHub.QueuedCommand>>();
            CardReaderHub._commandProcessors["IsConnected"] = new Action<CardReaderHub.QueuedCommand>(this.ProcessIsConnectedCommand);
            CardReaderHub._commandProcessors["GetUnitHealth"] = new Action<CardReaderHub.QueuedCommand>(this.ProcessGetUnitHealthCommand);
            CardReaderHub._commandProcessors["RebootCardReader"] = new Action<CardReaderHub.QueuedCommand>(this.ProcessRebootCardReaderCommand);
            CardReaderHub._commandProcessors["ReadConfiguration"] = new Action<CardReaderHub.QueuedCommand>(this.ProcessReadConfigurationCommand);
            CardReaderHub._commandProcessors["WriteConfiguration"] = new Action<CardReaderHub.QueuedCommand>(this.ProcessWriteConfigurationCommand);
            CardReaderHub._commandProcessors["ReadCard"] = new Action<CardReaderHub.QueuedCommand>(this.ProcessReadCardCommand);
            CardReaderHub._commandProcessors["ValidateVersion"] = new Action<CardReaderHub.QueuedCommand>(this.ProcessValidateVersionCommand);
            CardReaderHub._commandProcessors["CheckActivation"] = new Action<CardReaderHub.QueuedCommand>(this.ProcessCheckActivationCommand);
            CardReaderHub._commandProcessors["CheckDeviceStatus"] = new Action<CardReaderHub.QueuedCommand>(this.ProcessCheckDeviceStatusCommand);
            CardReaderHub._commandProcessors["GetCardInsertedStatus"] = new Action<CardReaderHub.QueuedCommand>(this.ProcessGetCardInsertedStatusCommand);
            CardReaderHub._commandProcessors["SupportsEMV"] = new Action<CardReaderHub.QueuedCommand>(this.ProcessSupportsEMVCommand);
        }

        private void ProcessQueuedCommands()
        {
            CardReaderHub.ActiveCommand = (CardReaderHub.QueuedCommand)null;
            int num = 0;
            lock (CardReaderHub._syncObject)
            {
                if (!CardReaderHub._commandQueuePaused)
                {
                    num = CardReaderHub._commandQueue.Count;
                    if (num > 0)
                        CardReaderHub._commandQueue.TryDequeue(out CardReaderHub.ActiveCommand);
                }
            }
            while (num > 0 && CardReaderHub.ActiveCommand != null)
            {
                ILogger<CardReaderHub> logger1 = this._logger;
                if (logger1 != null)
                    logger1.LogInformation(string.Format("Begining processing for queued command {0} with ID {1}", (object)CardReaderHub.ActiveCommand?.Command?.CommandName, (object)CardReaderHub.ActiveCommand?.Command.RequestId));
                Action<CardReaderHub.QueuedCommand> action;
                if (CardReaderHub._commandProcessors.TryGetValue(CardReaderHub.ActiveCommand?.Command?.CommandName, out action))
                {
                    this._proxy.StopHealthTimer();
                    action(CardReaderHub.ActiveCommand);
                }
                else
                {
                    ILogger<CardReaderHub> logger2 = this._logger;
                    if (logger2 != null)
                        logger2.LogError("ProcessQueuedCommands - error.  No handler for command " + CardReaderHub.ActiveCommand?.Command?.CommandName);
                }
                ILogger<CardReaderHub> logger3 = this._logger;
                if (logger3 != null)
                    logger3.LogInformation(string.Format("Finished processing for queued command {0} with ID {1}", (object)CardReaderHub.ActiveCommand?.Command?.CommandName, (object)CardReaderHub.ActiveCommand?.Command.RequestId));
                lock (CardReaderHub._syncObject)
                {
                    if (!CardReaderHub._commandQueuePaused)
                    {
                        num = CardReaderHub._commandQueue.Count;
                        if (num > 0)
                        {
                            List<string> stringList = new List<string>();
                            foreach (CardReaderHub.QueuedCommand command in CardReaderHub._commandQueue)
                                stringList.Add(command?.Command?.CommandName);
                            string str = string.Join(", ", stringList.ToArray());
                            ILogger<CardReaderHub> logger4 = this._logger;
                            if (logger4 != null)
                                logger4.LogInformation(string.Format("Queued Command count: {0}   Commands: {1}", (object)num, (object)str));
                            CardReaderHub._commandQueue.TryDequeue(out CardReaderHub.ActiveCommand);
                        }
                    }
                }
            }
            CardReaderHub.ActiveCommand = (CardReaderHub.QueuedCommand)null;
            string str1 = CardReaderHub._commandQueuePaused ? string.Format("paused.  Command queue count = {0}", (object)CardReaderHub._commandQueue.Count) : "empty";
            ILogger<CardReaderHub> logger = this._logger;
            if (logger != null)
                logger.LogInformation("Command queue is " + str1 + ".");
            this._proxy.StartHealthTimer();
        }

        private void ProcessIsConnectedCommand(CardReaderHub.QueuedCommand queuedCommand)
        {
            IIUC285Proxy proxy = this._proxy;
            bool flag = proxy != null && proxy.IsConnected;
            IsConnectedResponseEvent connectedResponseEvent1 = new IsConnectedResponseEvent(queuedCommand.Command);
            connectedResponseEvent1.IsConnected = flag;
            connectedResponseEvent1.Success = true;
            IsConnectedResponseEvent connectedResponseEvent2 = connectedResponseEvent1;
            this.LogEvent(">>> IsConnectedResponseEvent", (BaseEvent)connectedResponseEvent2);
            ClientProxyExtensions.SendAsync(queuedCommand.Caller, connectedResponseEvent2.EventName, (object)connectedResponseEvent2, new CancellationToken());
        }

        private void ProcessGetUnitHealthCommand(CardReaderHub.QueuedCommand queuedCommand)
        {
            UnitHealthModel unitHealth = this._proxy?.GetUnitHealth();
            GetUnitHealthResponseEvent healthResponseEvent1 = new GetUnitHealthResponseEvent(queuedCommand.Command);
            healthResponseEvent1.UnitHealthModel = unitHealth;
            healthResponseEvent1.Success = unitHealth != null;
            GetUnitHealthResponseEvent healthResponseEvent2 = healthResponseEvent1;
            this.LogEvent(">>> GetUnitHealthResponseEvent", (BaseEvent)healthResponseEvent2);
            ClientProxyExtensions.SendAsync(queuedCommand.Caller, healthResponseEvent2.EventName, (object)healthResponseEvent2, new CancellationToken());
        }

        private void ProcessRebootCardReaderCommand(CardReaderHub.QueuedCommand queuedCommand)
        {
            bool flag = this._proxy.Reboot();
            RebootCardReaderResponseEvent readerResponseEvent1 = new RebootCardReaderResponseEvent(queuedCommand.Command);
            readerResponseEvent1.Success = flag;
            RebootCardReaderResponseEvent readerResponseEvent2 = readerResponseEvent1;
            this.LogEvent(">>> Reboot Card Reader", (BaseEvent)readerResponseEvent2);
            ClientProxyExtensions.SendAsync(queuedCommand.Caller, readerResponseEvent2.EventName, (object)readerResponseEvent2, new CancellationToken());
        }

        private void ProcessReadConfigurationCommand(CardReaderHub.QueuedCommand queuedCommand)
        {
            ConfigCommand configCommand = queuedCommand.Command as ConfigCommand;
            string Data;
            Task<string> task = Task.Run<string>((Func<string>)(() => !this._proxy.ReadConfig(configCommand.GroupNumber, configCommand.IndexNumber, out Data) ? (string)null : Data));
            ((Task)task).Wait();
            SimpleResponseEvent simpleResponseEvent = new SimpleResponseEvent(queuedCommand.Command, "ReadConfiguration")
            {
                Data = task?.Result
            };
            this.LogEvent(">>> ReadConfiguration", (BaseEvent)simpleResponseEvent);
            ClientProxyExtensions.SendAsync(queuedCommand.Caller, "ReadConfiguration", (object)simpleResponseEvent, new CancellationToken());
        }

        private void ProcessWriteConfigurationCommand(CardReaderHub.QueuedCommand queuedCommand)
        {
            ConfigCommand configCommand = queuedCommand.Command as ConfigCommand;
            Task<bool> task = Task.Run<bool>((Func<bool>)(() => this._proxy.WriteConfig(configCommand.GroupNumber, configCommand.IndexNumber, configCommand.Value)));
            ((Task)task).Wait();
            SimpleResponseEvent simpleResponseEvent = new SimpleResponseEvent(queuedCommand.Command, "WriteConfiguration")
            {
                Data = task?.Result.ToString()
            };
            this.LogEvent(">>> WriteConfiguration", (BaseEvent)simpleResponseEvent);
            ClientProxyExtensions.SendAsync(queuedCommand.Caller, "WriteConfiguration", (object)simpleResponseEvent, new CancellationToken());
        }

        private void ProcessValidateVersionCommand(CardReaderHub.QueuedCommand queuedCommand)
        {
            if (!(queuedCommand?.Command is ValidateVersionCommand command))
                return;
            bool flag = DeviceService.Domain.DeviceService.IsClientVersionCompatible(command.DeviceServiceClientVersion);
            ValidateVersionResponseEvent versionResponseEvent1 = new ValidateVersionResponseEvent((BaseCommandRequest)command);
            versionResponseEvent1.ValidateVersionModel = new ValidateVersionModel()
            {
                IsCompatible = flag,
                DeviceServiceVersion = DeviceService.Domain.DeviceService.AssemblyVersion
            };
            versionResponseEvent1.Success = true;
            ValidateVersionResponseEvent versionResponseEvent2 = versionResponseEvent1;
            this.LogEvent(">>> validateVersionResponseEvent", (BaseEvent)versionResponseEvent2);
            ClientProxyExtensions.SendAsync(queuedCommand.Caller, "ValidateVersionReponseEvent", (object)versionResponseEvent2, new CancellationToken());
        }

        private void ProcessGetCardInsertedStatusCommand(CardReaderHub.QueuedCommand queuedCommand)
        {
            if (queuedCommand == null)
                return;
            InsertedStatus insertedStatus = this._proxy.CheckIfCardInserted();
            GetCardInsertedStatusResponseEvent statusResponseEvent1 = new GetCardInsertedStatusResponseEvent(queuedCommand.Command);
            statusResponseEvent1.CardInsertedStatus = insertedStatus;
            statusResponseEvent1.Success = true;
            GetCardInsertedStatusResponseEvent statusResponseEvent2 = statusResponseEvent1;
            this.LogEvent(">>> GetCardInsertedStatusResponseEvent", (BaseEvent)statusResponseEvent2);
            ClientProxyExtensions.SendAsync(queuedCommand.Caller, "GetCardInsertedStatusResponseEvent", (object)statusResponseEvent2, new CancellationToken());
        }

        private void ProcessSupportsEMVCommand(CardReaderHub.QueuedCommand queuedCommand)
        {
            if (queuedCommand == null)
                return;
            bool supportsEmv = this._proxy.SupportsEMV;
            SupportsEMVResponseEvent emvResponseEvent1 = new SupportsEMVResponseEvent(queuedCommand.Command);
            emvResponseEvent1.Success = true;
            emvResponseEvent1.SuportsEMV = supportsEmv;
            SupportsEMVResponseEvent emvResponseEvent2 = emvResponseEvent1;
            this.LogEvent(">>> SupportsEMVResponseEvent", (BaseEvent)emvResponseEvent2);
            ClientProxyExtensions.SendAsync(queuedCommand.Caller, "SupportsEMVResponseEvent", (object)emvResponseEvent2, new CancellationToken());
        }

        private void ProcessCheckActivationCommand(CardReaderHub.QueuedCommand queuedCommand)
        {
            if (!(queuedCommand?.Command is CheckActivationCommand command))
                return;
            ILogger<CardReaderHub> logger = this._logger;
            if (logger != null)
                logger.LogDebug(string.Format("CheckActivation called for Kiosk ID {0}", (object)command.Request.KioskId));
            bool result = this._activationService.CheckAndActivate((IBluefinActivationRequest)command.Request).Result;
            CheckActivationResponseEvent activationResponseEvent1 = new CheckActivationResponseEvent((BaseCommandRequest)command);
            activationResponseEvent1.Success = result;
            CheckActivationResponseEvent activationResponseEvent2 = activationResponseEvent1;
            this.LogEvent(">>> checkActivationResponseEvent", (BaseEvent)activationResponseEvent2);
            ClientProxyExtensions.SendAsync(queuedCommand.Caller, "CheckActivationResponseEvent", (object)activationResponseEvent2, new CancellationToken());
        }

        private async void ProcessCheckDeviceStatusCommand(CardReaderHub.QueuedCommand queuedCommand)
        {
            if (!(queuedCommand.Command is CheckDeviceStatusCommand command))
                return;
            CheckDeviceStatusResponseEvent responseEvent = new CheckDeviceStatusResponseEvent((BaseCommandRequest)command);
            try
            {
                StandardResponse standardResponse;
                if (command.Request != null)
                    standardResponse = await this._deviceStatusService.PostDeviceStatus(command.Request);
                else
                    standardResponse = await this._deviceStatusService.PostDeviceStatus();
                responseEvent.Success = standardResponse.Success && standardResponse.StatusCode == 200;
            }
            catch (Exception ex)
            {
                ILogger<CardReaderHub> logger = this._logger;
                if (logger != null)
                    logger.LogError(ex, "ProcessCheckDeviceStatusCommand Error: " + ex.Message);
                responseEvent.Success = false;
            }
            await ClientProxyExtensions.SendAsync(queuedCommand.Caller, responseEvent.EventName, (object)responseEvent, new CancellationToken());
            responseEvent = (CheckDeviceStatusResponseEvent)null;
        }

        private async void ProcessReadCardCommand(CardReaderHub.QueuedCommand queuedCommand)
        {
            ReadCardCommand command = queuedCommand?.Command as ReadCardCommand;
            if (command == null)
                return;
            ILogger<CardReaderHub> logger1 = this._logger;
            if (logger1 != null)
                logger1.LogInformation("<<<CardReaderHub.ExecuteBaseCommand " + JsonConvert.SerializeObject((object)command));
            if (queuedCommand.Cancelled)
            {
                ILogger<CardReaderHub> logger2 = this._logger;
                if (logger2 == null)
                    return;
                logger2.LogInformation(string.Format("Queued ReadCardCommand not run because it was Canceled.  RequestId: {0}", (object)command.RequestId));
            }
            else
            {
                CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
                CardReaderHub._cancellationTokenSources[command.RequestId] = cancellationTokenSource;
                CancellationToken cancellationToken = cancellationTokenSource.Token;
                int num = await this._proxy.ReadCard((ICardReadRequest)command.Request, cancellationToken, (Action<Base87CardReadModel>)(cardResult =>
                {
                    IClientProxy caller = queuedCommand.Caller;
                    if (caller != null)
                    {
                        BaseResponseEvent responseEvent;
                        switch (cardResult)
                        {
                            case EMVCardReadModel emvCardReadModel2:
                                responseEvent = (BaseResponseEvent)new EMVCardReadResponseEvent((BaseCommandRequest)command)
                                {
                                    Data = emvCardReadModel2
                                };
                                this.ProcessCardReadModel(responseEvent, cancellationToken);
                                break;
                            case EncryptedCardReadModel encryptedCardReadModel2:
                                responseEvent = (BaseResponseEvent)new EncryptedCardReadResponseEvent((BaseCommandRequest)command)
                                {
                                    Data = encryptedCardReadModel2
                                };
                                this.ProcessCardReadModel(responseEvent, cancellationToken);
                                break;
                            default:
                                responseEvent = (BaseResponseEvent)new UnencryptedCardReadResponseEvent((BaseCommandRequest)command)
                                {
                                    Data = (cardResult as UnencryptedCardReadModel)
                                };
                                this.ProcessCardReadModel(responseEvent, cancellationToken);
                                break;
                        }
                        ClientProxyExtensions.SendAsync(caller, responseEvent.EventName, (object)responseEvent, new CancellationToken());
                    }
                    else
                        this._logger.LogInformation(string.Format("Unable to find active read card client proxy for {0}.", (object)command?.RequestId));
                }), (Action<string, string>)((eventName, eventData) =>
                {
                    IClientProxy caller = queuedCommand.Caller;
                    if (caller != null)
                    {
                        switch (eventName)
                        {
                            case "CardRemovedResponseEvent":
                                ClientProxyExtensions.SendAsync(caller, "CardRemovedResponseEvent", (object)new CardRemoveResponseEvent((BaseCommandRequest)command), new CancellationToken());
                                break;
                            default:
                                ClientProxyExtensions.SendAsync(caller, eventName, (object)new SimpleResponseEvent((BaseCommandRequest)command, eventName, eventData), new CancellationToken());
                                break;
                        }
                    }
                    else
                        this._logger.LogInformation(string.Format("Unable to find remove card client proxy for {0}.", (object)command?.RequestId));
                })) ? 1 : 0;
                CardReaderHub._cancellationTokenSources.TryRemove(command.RequestId, out CancellationTokenSource _);
            }
        }

        public void ExecuteBaseCommand(BaseCommandRequest command)
        {
            if (command == null)
                return;
            ILogger<CardReaderHub> logger = this._logger;
            if (logger != null)
                logger.LogInformation("<<< " + command?.CommandName + " Command " + JsonConvert.SerializeObject((object)command));
            this.AddQueuedCommand(new CardReaderHub.QueuedCommand()
            {
                Command = command,
                Caller = ((IHubCallerClients<IClientProxy>)this.Clients).Caller
            });
        }

        public void ExecuteDeviceServiceShutDownCommand(DeviceServiceShutDownCommand command)
        {
            if (command == null)
                return;
            ILogger<CardReaderHub> logger = this._logger;
            if (logger != null)
                logger.LogInformation("<<< " + command?.CommandName + " Command " + JsonConvert.SerializeObject((object)command));
            IClientProxy clientProxy = ((IHubCallerClients<IClientProxy>)this.Clients)?.Caller;
            if (this._applicationControl == null)
                return;
            Task.Run((Action)(() =>
            {
                bool flag = this._applicationControl.ShutDown(command.ForceShutDown, command.Reason);
                DeviceServiceShutDownResponseEvent downResponseEvent = new DeviceServiceShutDownResponseEvent((BaseCommandRequest)command)
                {
                    Success = flag
                };
                this.LogEvent(">>> DeviceServiceShutDownResponseEvent", (BaseEvent)downResponseEvent);
                IClientProxy iclientProxy = clientProxy;
                if (iclientProxy == null)
                    return;
                ClientProxyExtensions.SendAsync(iclientProxy, "DeviceServiceShutDownResponseEvent", (object)downResponseEvent, new CancellationToken());
            }));
        }

        public void ExecuteDeviceServiceCanShutDownCommand(DeviceServiceCanShutDownCommand command)
        {
            if (command == null)
                return;
            ILogger<CardReaderHub> logger = this._logger;
            if (logger != null)
                logger.LogInformation("<<< " + command?.CommandName + " Command " + JsonConvert.SerializeObject((object)command));
            if (command == null || this._applicationControl == null)
                return;
            this._applicationControl.SetCanShutDownClientResponse(command.CanShutDown);
        }

        public void SetCommandQueueState(SetCommandQueueStateCommand command)
        {
            if (command == null)
                return;
            ILogger<CardReaderHub> logger = this._logger;
            if (logger != null)
                logger.LogInformation("<<< " + command?.CommandName + " Command " + JsonConvert.SerializeObject((object)command));
            this.CommandQueuePaused = command.CommandQueuePaused;
        }

        public void ExecuteReadCardCommand(ReadCardCommand command)
        {
            if (command == null)
                return;
            ILogger<CardReaderHub> logger = this._logger;
            if (logger != null)
                logger.LogInformation("<<< ReadCardCommand " + JsonConvert.SerializeObject((object)command));
            this.AddQueuedCommand(new CardReaderHub.QueuedCommand()
            {
                Command = (BaseCommandRequest)command,
                Caller = ((IHubCallerClients<IClientProxy>)this.Clients).Caller
            });
        }

        private void ProcessCardReadModel(
          BaseResponseEvent responseEvent,
          CancellationToken cancellationToken)
        {
            ICardReadResponseEvent readResponseEvent1 = responseEvent as ICardReadResponseEvent;
            if (cancellationToken.IsCancellationRequested)
            {
                responseEvent.Success = false;
                if (readResponseEvent1 != null)
                {
                    if (readResponseEvent1.GetBase87CardReadModel() == null && responseEvent is UnencryptedCardReadResponseEvent readResponseEvent2)
                        readResponseEvent2.Data = new UnencryptedCardReadModel();
                    readResponseEvent1.GetBase87CardReadModel().Status = ResponseStatus.Cancelled;
                }
            }
            BaseResponseEvent baseResponseEvent = responseEvent;
            int num;
            if (readResponseEvent1 == null)
            {
                num = 0;
            }
            else
            {
                ResponseStatus? status = readResponseEvent1.GetBase87CardReadModel()?.Status;
                ResponseStatus responseStatus = ResponseStatus.Success;
                num = status.GetValueOrDefault() == responseStatus & status.HasValue ? 1 : 0;
            }
            baseResponseEvent.Success = num != 0;
            this.LogEvent(string.Format("CardReaderHub.ExecuteBaseCommand {0}", (object)responseEvent?.GetType()), (BaseEvent)responseEvent);
        }

        public void ExecuteCancelCommand(CancelCommandRequest cancelCommandRequest)
        {
            if (cancelCommandRequest == null)
                return;
            ILogger<CardReaderHub> logger1 = this._logger;
            if (logger1 != null)
                logger1.LogInformation("CardReaderHub.ExecuteCancelCommand " + JsonConvert.SerializeObject((object)cancelCommandRequest));
            bool flag = false;
            CancellationTokenSource cancellationTokenSource;
            CardReaderHub._cancellationTokenSources.TryGetValue(cancelCommandRequest.CommandToCancelRequestId, out cancellationTokenSource);
            if (cancellationTokenSource != null)
            {
                cancellationTokenSource.Cancel();
                this._proxy.ReadCancel();
                flag = true;
            }
            else
            {
                lock (CardReaderHub._syncObject)
                {
                    foreach (CardReaderHub.QueuedCommand command in CardReaderHub._commandQueue)
                    {
                        Guid? requestId = command?.Command.RequestId;
                        Guid? nullable1 = cancelCommandRequest?.CommandToCancelRequestId;
                        if ((requestId.HasValue == nullable1.HasValue ? (requestId.HasValue ? (requestId.GetValueOrDefault() == nullable1.GetValueOrDefault() ? 1 : 0) : 1) : 0) != 0)
                        {
                            command.Cancelled = true;
                            ILogger<CardReaderHub> logger2 = this._logger;
                            if (logger2 != null)
                            {
                                Guid? nullable2;
                                if (command == null)
                                {
                                    nullable1 = new Guid?();
                                    nullable2 = nullable1;
                                }
                                else
                                    nullable2 = new Guid?(command.Command.RequestId);
                                logger2.LogInformation(string.Format("Canceled queued command {0}", (object)nullable2));
                            }
                            flag = true;
                        }
                    }
                }
            }
            CancelCommandResponseEvent commandResponseEvent1 = new CancelCommandResponseEvent((BaseCommandRequest)cancelCommandRequest);
            commandResponseEvent1.Success = flag;
            CancelCommandResponseEvent commandResponseEvent2 = commandResponseEvent1;
            this.LogEvent("CardReaderHub.ExecuteCancelCommand cancelCommandResponseEvent", (BaseEvent)commandResponseEvent2);
            ClientProxyExtensions.SendAsync(((IHubCallerClients<IClientProxy>)this.Clients).Caller, "CancelCommandResponseEvent", (object)commandResponseEvent2, new CancellationToken());
        }

        public void SendShutDownStartingEvent(
          DeviceServiceShutDownStartingEvent deviceServiceShutDownStartingEvent)
        {
            if (deviceServiceShutDownStartingEvent == null)
                return;
            ILogger<CardReaderHub> logger = this._logger;
            if (logger != null)
                logger.LogInformation(">>> SendShutDownStartingEvent Event " + JsonConvert.SerializeObject((object)deviceServiceShutDownStartingEvent));
            ClientProxyExtensions.SendAsync(((IHubClients<IClientProxy>)this.Clients).All, "DeviceServiceShutDownStartingEvent", (object)deviceServiceShutDownStartingEvent, new CancellationToken());
        }

        public void SendCardReaderStateEvent(CardReaderStateEvent cardReaderStateEvent)
        {
            if (cardReaderStateEvent == null)
                return;
            ILogger<CardReaderHub> logger = this._logger;
            if (logger != null)
                logger.LogInformation(">>> SendCardReaderStateEvent " + JsonConvert.SerializeObject((object)cardReaderStateEvent));
            ClientProxyExtensions.SendAsync(((IHubClients<IClientProxy>)this.Clients).All, "CardReaderStateEvent", (object)cardReaderStateEvent, new CancellationToken());
        }

        public void ExecuteValidateVersionCommand(ValidateVersionCommand validateVersionCommand)
        {
            if (validateVersionCommand == null)
                return;
            ILogger<CardReaderHub> logger = this._logger;
            if (logger != null)
                logger.LogInformation("<<< ValidateVersion Command " + JsonConvert.SerializeObject((object)validateVersionCommand));
            this.AddQueuedCommand(new CardReaderHub.QueuedCommand()
            {
                Command = (BaseCommandRequest)validateVersionCommand,
                Caller = ((IHubCallerClients<IClientProxy>)this.Clients).Caller
            });
        }

        public void ExecuteCheckActivationCommand(CheckActivationCommand checkActivaionCommand)
        {
            if (checkActivaionCommand == null)
                return;
            ILogger<CardReaderHub> logger = this._logger;
            if (logger != null)
                logger.LogInformation(string.Format("<<< CheckActivation Command {0}", checkActivaionCommand.Scrub()));
            this.AddQueuedCommand(new CardReaderHub.QueuedCommand()
            {
                Command = (BaseCommandRequest)checkActivaionCommand,
                Caller = ((IHubCallerClients<IClientProxy>)this.Clients).Caller
            });
        }

        public void ExecuteCheckDeviceStatusCommand(CheckDeviceStatusCommand checkDeviceStatusCommand)
        {
            if (checkDeviceStatusCommand == null)
                return;
            ILogger<CardReaderHub> logger = this._logger;
            if (logger != null)
                logger.LogInformation("<<< CheckDeviceStatus Command " + JsonConvert.SerializeObject(checkDeviceStatusCommand.Scrub()));
            this.AddQueuedCommand(new CardReaderHub.QueuedCommand()
            {
                Command = (BaseCommandRequest)checkDeviceStatusCommand,
                Caller = ((IHubCallerClients<IClientProxy>)this.Clients).Caller
            });
        }

        public void ExecuteReportAuthorizeResultCommand(
          ReportAuthorizeResultCommand reportAuthorizeResultCommand)
        {
            if (reportAuthorizeResultCommand == null)
                return;
            ILogger<CardReaderHub> logger = this._logger;
            if (logger != null)
                logger.LogInformation("<<< ReportAuthorizeResult Command " + JsonConvert.SerializeObject((object)reportAuthorizeResultCommand));
            bool success = reportAuthorizeResultCommand.Success;
            this._proxy.SetAuthorizationResponse(success);
            ReportAuthorizationResultResponseEvent resultResponseEvent1 = new ReportAuthorizationResultResponseEvent((BaseCommandRequest)reportAuthorizeResultCommand);
            resultResponseEvent1.Success = success;
            ReportAuthorizationResultResponseEvent resultResponseEvent2 = resultResponseEvent1;
            this.LogEvent(">>> ReportAuthorizeResultResponseEvent", (BaseEvent)resultResponseEvent2);
            ClientProxyExtensions.SendAsync(((IHubCallerClients<IClientProxy>)this.Clients).Caller, "ReportAuthorizationResultResponseEvent", (object)resultResponseEvent2, new CancellationToken());
        }

        private void LogEvent(string eventContext, BaseEvent baseEvent)
        {
            if (baseEvent == null)
                return;
            string logText = (string)null;
            string str = !(baseEvent is EMVCardReadResponseEvent responseEvent1) ? (!(baseEvent is EncryptedCardReadResponseEvent responseEvent2) ? (!(baseEvent is UnencryptedCardReadResponseEvent responseEvent3) ? JsonConvert.SerializeObject((object)baseEvent) : this.ObfuscateUnencryptedCardReadResponseEvent(responseEvent3, logText)) : this.ObfuscateEncryptedCardReadResponseEvent(responseEvent2, logText)) : this.ObfuscateEMVCardReadResponseEvent(responseEvent1, logText);
            ILogger<CardReaderHub> logger = this._logger;
            if (logger == null)
                return;
            logger.LogInformation(eventContext + " " + str);
        }

        private string ObfuscateEMVCardReadResponseEvent(
          EMVCardReadResponseEvent responseEvent,
          string logText)
        {
            return this.CreateResponseEventLogText((BaseEvent)responseEvent, (Func<string, object>)(json =>
            {
                EMVCardReadResponseEvent readResponseEvent = JsonConvert.DeserializeObject<EMVCardReadResponseEvent>(json);
                if (readResponseEvent == null)
                    return (object)readResponseEvent;
                EMVCardReadModel data = readResponseEvent.Data;
                if (data == null)
                    return (object)readResponseEvent;
                data.ObfuscateSensitiveData();
                return (object)readResponseEvent;
            }));
        }

        private string ObfuscateEncryptedCardReadResponseEvent(
          EncryptedCardReadResponseEvent responseEvent,
          string logText)
        {
            return this.CreateResponseEventLogText((BaseEvent)responseEvent, (Func<string, object>)(json =>
            {
                EncryptedCardReadResponseEvent readResponseEvent = JsonConvert.DeserializeObject<EncryptedCardReadResponseEvent>(json);
                if (readResponseEvent == null)
                    return (object)readResponseEvent;
                EncryptedCardReadModel data = readResponseEvent.Data;
                if (data == null)
                    return (object)readResponseEvent;
                data.ObfuscateSensitiveData();
                return (object)readResponseEvent;
            }));
        }

        private string ObfuscateUnencryptedCardReadResponseEvent(
          UnencryptedCardReadResponseEvent responseEvent,
          string logText)
        {
            return this.CreateResponseEventLogText((BaseEvent)responseEvent, (Func<string, object>)(json =>
            {
                UnencryptedCardReadResponseEvent readResponseEvent = JsonConvert.DeserializeObject<UnencryptedCardReadResponseEvent>(json);
                if (readResponseEvent == null)
                    return (object)readResponseEvent;
                UnencryptedCardReadModel data = readResponseEvent.Data;
                if (data == null)
                    return (object)readResponseEvent;
                data.ObfuscateSensitiveData();
                return (object)readResponseEvent;
            }));
        }

        private string CreateResponseEventLogText(
          BaseEvent responseEvent,
          Func<string, object> ObfuscateJsonAction)
        {
            string responseEventLogText = (string)null;
            try
            {
                string str = JsonConvert.SerializeObject((object)responseEvent, new JsonSerializerSettings()
                {
                    ReferenceLoopHandling = ReferenceLoopHandling.Ignore
                });
                responseEventLogText = JsonConvert.SerializeObject(ObfuscateJsonAction(str));
            }
            catch (Exception ex)
            {
                this._logger.LogError(ex, "Unhandled exception in CardReaderHub.CreateResponseEventLogText while deserializing a CardReadResponseEvent");
            }
            return responseEventLogText;
        }

        public virtual Task OnConnectedAsync()
        {
            this._logger.LogInformation("CardReaderHub.OnConnectedAsync event");
            this._analytics?.ClientConnectedToHub();
            this.SendMessage("Hello");
            if (this._proxy?.UnitData?.IsTampered ?? false)
                this.SendSimpleEvent((SimpleEvent)new DeviceTamperedEvent());
            CardReaderState cardReaderState = this._proxy?.GetCardReaderState();
            if (cardReaderState != null)
                this.SendCardReaderStateEvent(new CardReaderStateEvent()
                {
                    CardReaderState = cardReaderState
                });
            return base.OnConnectedAsync();
        }

        private void ClearCommands()
        {
            this.CommandQueuePaused = true;
            ConcurrentQueue<CardReaderHub.QueuedCommand> concurrentQueue = new ConcurrentQueue<CardReaderHub.QueuedCommand>();
            foreach (CardReaderHub.QueuedCommand command in CardReaderHub._commandQueue)
            {
                if (command.ConnectionId != this.Context.ConnectionId)
                {
                    concurrentQueue.Enqueue(command);
                }
                else
                {
                    CancellationTokenSource cancellationTokenSource;
                    CardReaderHub._cancellationTokenSources.TryRemove(command.Command.RequestId, out cancellationTokenSource);
                    cancellationTokenSource?.Cancel();
                }
            }
            CardReaderHub._commandQueue = concurrentQueue;
            if (CardReaderHub.ActiveCommand?.ConnectionId == this.Context.ConnectionId)
            {
                CardReaderHub._cancellationTokenSources[CardReaderHub.ActiveCommand.Command.RequestId].Cancel();
                CardReaderHub.ActiveCommand.Cancelled = true;
                if (CardReaderHub.ActiveCommand.Command.SignalRCommand == "ExecuteReadCardCommand")
                    this._proxy.ReadCancel();
            }
            this.CommandQueuePaused = false;
        }

        public virtual Task OnDisconnectedAsync(Exception exception)
        {
            this._logger.LogInformation("CardReaderHub.OnDisconnectedAsync event");
            this._analytics?.ClientDisconnectedFromHub();
            if (exception != null)
                this._logger.LogError(exception, "CardReaderHub.OnDisconnectedAsync - Unhandled exception.");
            this.ClearCommands();
            return base.OnDisconnectedAsync(exception);
        }

        private class QueuedCommand
        {
            public BaseCommandRequest Command { get; set; }

            public string ConnectionId { get; set; }

            public IClientProxy Caller { get; set; }

            public bool Cancelled { get; set; }
        }
    }
}
