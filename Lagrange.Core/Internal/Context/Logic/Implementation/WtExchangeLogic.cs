using System.Text.Json;
using Lagrange.Core.Common;
using Lagrange.Core.Event.EventArg;
using Lagrange.Core.Internal.Context.Attributes;
using Lagrange.Core.Internal.Event;
using Lagrange.Core.Internal.Event.Login;
using Lagrange.Core.Internal.Event.System;
using Lagrange.Core.Internal.Packets.Login.NTLogin;
using Lagrange.Core.Internal.Packets.Login.WtLogin.Entity;
using Lagrange.Core.Internal.Service;
using Lagrange.Core.Utility.Crypto;
using Lagrange.Core.Utility.Network;

// ReSharper disable AsyncVoidLambda

namespace Lagrange.Core.Internal.Context.Logic.Implementation;

[EventSubscribe(typeof(TransEmpEvent))]
[EventSubscribe(typeof(LoginEvent))]
[EventSubscribe(typeof(KickNTEvent))]
[BusinessLogic("WtExchangeLogic", "Manage the online task of the Bot")]
internal class WtExchangeLogic : LogicBase
{
    private const string Tag = nameof(WtExchangeLogic);

    private readonly TaskCompletionSource<bool> _qrCodeTask;
    private readonly TaskCompletionSource<bool> _unusualTask;
    private TaskCompletionSource<(string, string)>? _captchaTask;

    private const string Interface = "https://ntlogin.qq.com/qr/getFace";

    private const string QueryEvent = "wtlogin.trans_emp CMD0x12";

    internal WtExchangeLogic(ContextCollection collection) : base(collection)
    {
        _qrCodeTask = new TaskCompletionSource<bool>();
        _unusualTask = new TaskCompletionSource<bool>();
    }

    public override async Task Incoming(ProtocolEvent e)
    {
        switch (e)
        {
            case KickNTEvent kick:
                Collection.Log.LogFatal(Tag, $"KickNTEvent: {kick.Tag}: {kick.Message}");
                Collection.Log.LogFatal(Tag, "Bot will be offline in 5 seconds...");
                await Task.Delay(5000);
                
                Collection.Invoker.PostEvent(new BotOfflineEvent()); // TODO: Fill in the reason of offline
                Collection.Scheduler.Dispose();
                break;
        }
    }

    /// <summary>
    /// <para>1. resolve wtlogin.trans_emp CMD0x31 packet</para>
    /// <para>2. Schedule wtlogin.trans_emp CMD0x12 Task</para>
    /// </summary>
    public async Task<(string, byte[])?> FetchQrCode()
    {
        Collection.Log.LogInfo(Tag, "Connecting Servers...");
        if (!await Collection.Socket.Connect()) return null;
        Collection.Scheduler.Interval("Heartbeat.Alive", 10 * 1000, async () => await Collection.Business.PushEvent(AliveEvent.Create()));
        
        var transEmp = TransEmpEvent.Create(TransEmpEvent.State.FetchQrCode);
        var result = await Collection.Business.SendEvent(transEmp);

        if (result.Count != 0)
        {
            var @event = (TransEmpEvent)result[0];
            Collection.Keystore.Session.QrString = @event.QrSig;
            Collection.Keystore.Session.QrSign = @event.Signature;
            Collection.Keystore.Session.QrUrl = @event.Url;
            
            Collection.Log.LogInfo(Tag, $"QrCode Fetched, Expiration: {@event.Expiration} seconds");
            return (@event.Url, @event.QrCode);
        }
        return null;
    }

    public Task LoginByQrCode()
    {
        Collection.Scheduler.Interval(QueryEvent, 2 * 1000, async () => await QueryQrCodeState());
        return _qrCodeTask.Task;
    }

    public async Task<bool> LoginByPassword()
    {
        if (!Collection.Socket.Connected) // if socket not connected, try to connect
        {        
            if (!await Collection.Socket.Connect()) return false;
            Collection.Scheduler.Interval("Heartbeat.Alive", 10 * 1000, async () => await Collection.Business.PushEvent(AliveEvent.Create()));
        }

        if (Collection.Keystore.Session.ExchangeKey == null)
        {
            if (!await KeyExchange())
            {
                Collection.Log.LogInfo(Tag, "Key Exchange Failed, please try again later");
                return false;
            }
        }

        if (Collection.Keystore.Session.TempPassword != null) // try EasyLogin
        {
            Collection.Log.LogInfo(Tag, "Trying to Login by EasyLogin...");
            var easyLoginEvent = EasyLoginEvent.Create();
            var easyLoginResult = await Collection.Business.SendEvent(easyLoginEvent);

            if (easyLoginResult.Count != 0)
            {
                switch ((LoginCommon.Error)easyLoginResult[0].ResultCode)
                {
                    case LoginCommon.Error.Success:
                    {
                        Collection.Log.LogInfo(Tag, "Login Success");

                        await BotOnline();
                        return true;
                    }
                    case LoginCommon.Error.UnusualVerify:
                    {
                        Collection.Log.LogInfo(Tag, "Login Success, but need to verify");

                        if (!await FetchUnusual())
                        {
                            Collection.Log.LogInfo(Tag, "Fetch unusual state failed");
                            return false;
                        }
                        
                        Collection.Scheduler.Interval(QueryEvent, 2 * 1000, async () => await QueryUnusualState());
                        bool result = await _unusualTask.Task;
                        if (result) await BotOnline();
                        return result;
                    }
                    default:
                    {
                        Collection.Log.LogWarning(Tag, "Fast Login Failed, trying to Login by Password...");
                        
                        Collection.Keystore.Session.TempPassword = null; // clear temp password
                        return await LoginByPassword(); // try password login
                    }
                }
            }
        }
        else
        {
            Collection.Log.LogInfo(Tag, "Trying to Login by Password...");
            var passwordLoginEvent = PasswordLoginEvent.Create();
            var passwordLoginResult = await Collection.Business.SendEvent(passwordLoginEvent);

            if (passwordLoginResult.Count != 0)
            {
                var @event = (PasswordLoginEvent)passwordLoginResult[0];
                switch ((LoginCommon.Error)@event.ResultCode)
                {
                    case LoginCommon.Error.Success:
                    {
                        Collection.Log.LogInfo(Tag, "Login Success");

                        await BotOnline();
                        return true;
                    }
                    case LoginCommon.Error.UnusualVerify:
                    {
                        Collection.Log.LogInfo(Tag, "Login Success, but need to verify");

                        await FetchUnusual();
                        Collection.Scheduler.Interval(QueryEvent, 2 * 1000, async () => await QueryUnusualState());
                        return true;
                    }
                    case LoginCommon.Error.CaptchaVerify:
                    {
                        Collection.Log.LogInfo(Tag, "Login Success, but captcha is required, please follow the link from event");
                        if (Collection.Keystore.Session.CaptchaUrl != null)
                        {
                            var captchaEvent = new BotCaptchaEvent(Collection.Keystore.Session.CaptchaUrl);
                            Collection.Invoker.PostEvent(captchaEvent);
                            
                            string aid = Collection.Keystore.Session.CaptchaUrl.Split("&sid=")[1].Split("&")[0];
                            _captchaTask = new TaskCompletionSource<(string, string)>();
                            var (ticket, randStr) = await _captchaTask.Task;
                            Collection.Keystore.Session.Captcha = new ValueTuple<string, string, string>(ticket, randStr, aid);

                            return await LoginByPassword();
                        }
                        
                        Collection.Log.LogInfo(Tag, "Captcha Url is null, please try again later");
                        return false;
                    }
                    default:
                    {
                        Collection.Log.LogWarning(Tag, @event is { Message: not null, Tag: not null }
                            ? $"Login Failed: {(LoginCommon.Error)@event.ResultCode} | {@event.Tag}: {@event.Message}"
                            : $"Login Failed: {(LoginCommon.Error)@event.ResultCode}");
                        
                        Collection.Invoker.Dispose();
                        return false;
                    }
                }
            }
        }

        return false;
    }

    private async Task<bool> KeyExchange()
    {
        var keyExchangeEvent = KeyExchangeEvent.Create();
        var exchangeResult = await Collection.Business.SendEvent(keyExchangeEvent);
        if (exchangeResult.Count != 0)
        {
            Collection.Log.LogInfo(Tag, "Key Exchange successfully!");
            return true;
        }

        return false;
    }

    private async Task<bool> DoWtLogin()
    {
        Collection.Log.LogInfo(Tag, "Doing Login...");
        Collection.Keystore.Session.Sequence = 0;

        Collection.Keystore.SecpImpl = new EcdhImpl(EcdhImpl.CryptMethod.Secp192K1);
        var loginEvent = LoginEvent.Create();
        var result = await Collection.Business.SendEvent(loginEvent);
        
        if (result.Count != 0)
        {
            var @event = (LoginEvent)result[0];
            if (@event.ResultCode == 0)
            {
                Collection.Log.LogInfo(Tag, "Login Success");
                Collection.Keystore.Info = new BotKeystore.BotInfo(@event.Age, @event.Sex, @event.Name);
                Collection.Log.LogInfo(Tag, Collection.Keystore.Info.ToString());
                await BotOnline();

                return true;
            }

            Collection.Log.LogFatal(Tag, $"Login failed: {@event.ResultCode}");
            Collection.Log.LogFatal(Tag, $"Tag: {@event.Tag}\nState: {@event.Message}");
        }
        
        return false;
    }

    private async Task QueryQrCodeState()
    {
        if (Collection.Keystore.Session.QrString == null)
        {
            Collection.Log.LogFatal(Tag, "QrString is null, Please Fetch QrCode First");
            _qrCodeTask.SetResult(false);
            return;
        }

        var request = new NTLoginHttpRequest
        {
            Appid = Collection.AppInfo.AppId,
            Qrsig = Collection.Keystore.Session.QrString,
            FaceUpdateTime = 0
        };
        var payload = JsonSerializer.SerializeToUtf8Bytes(request);
        var response = await Http.PostAsync(Interface, payload, "application/json");
        var info = JsonSerializer.Deserialize<NTLoginHttpResponse>(response);
        if (info != null) Collection.Keystore.Uin = info.Uin;

        var transEmp = TransEmpEvent.Create(TransEmpEvent.State.QueryResult);
        var result = await Collection.Business.SendEvent(transEmp);

        if (result.Count != 0)
        {
            var @event = (TransEmpEvent)result[0];
            var state = (TransEmp12.State)@event.ResultCode;
            Collection.Log.LogInfo(Tag, $"QrCode State Queried: {state} Uin: {Collection.Keystore.Uin}");

            switch (state)
            {
                case TransEmp12.State.Confirmed:
                {
                    Collection.Log.LogInfo(Tag, "QrCode Confirmed, Logging in with A1 sig...");
                    Collection.Scheduler.Cancel(QueryEvent); // cancel query task

                    if (@event.TgtgtKey != null)
                    {
                        Collection.Keystore.Stub.TgtgtKey = @event.TgtgtKey;
                        Collection.Keystore.Session.TempPassword = @event.TempPassword;
                        Collection.Keystore.Session.NoPicSig = @event.NoPicSig;

                        _qrCodeTask.SetResult(await DoWtLogin());
                    }
                    break;
                }
                case TransEmp12.State.CodeExpired:
                {
                    Collection.Log.LogWarning(Tag, "QrCode Expired, Please Fetch QrCode Again");
                    Collection.Scheduler.Cancel(QueryEvent);
                    Collection.Scheduler.Dispose();

                    _qrCodeTask.SetResult(false);
                    return;
                }
                case TransEmp12.State.Canceled:
                {
                    Collection.Log.LogWarning(Tag, "QrCode Canceled, Please Fetch QrCode Again");
                    Collection.Scheduler.Cancel(QueryEvent);
                    Collection.Scheduler.Dispose();

                    _qrCodeTask.SetResult(false);
                    return;
                }
                case TransEmp12.State.WaitingForConfirm: 
                case TransEmp12.State.WaitingForScan:
                default:
                    break;
            }
        }

    }

    private async Task BotOnline()
    {
        var onlineEvent = new BotOnlineEvent();
        Collection.Invoker.PostEvent(onlineEvent);
        
        var registerEvent = StatusRegisterEvent.Create();
        var registerResponse = await Collection.Business.SendEvent(registerEvent);
        var heartbeatDelegate = new Action(async () => await Collection.Business.PushEvent(SsoAliveEvent.Create()));
        Collection.Log.LogInfo(Tag, $"Register Status: {((StatusRegisterEvent)registerResponse[0]).Message}");
        Collection.Scheduler.Interval("SsoHeartBeat", (int)(4.5 * 60 * 1000), heartbeatDelegate);

        await Collection.Business.PushEvent(InfoSyncEvent.Create());
    }

    private async Task<bool> FetchUnusual()
    {
        var transEmp = TransEmpEvent.Create(TransEmpEvent.State.FetchQrCode);
        var result = await Collection.Business.SendEvent(transEmp);

        if (result.Count != 0)
        {
            Collection.Log.LogInfo(Tag, "Confirmation Request Send");
            return true;
        }

        return false;
    }

    private async Task QueryUnusualState()
    {
        var transEmp = TransEmpEvent.Create(TransEmpEvent.State.QueryResult);
        var result = await Collection.Business.SendEvent(transEmp);
        
        if (result.Count != 0)
        {
            var @event = (TransEmpEvent)result[0];
            var state = (TransEmp12.State)@event.ResultCode;
            Collection.Log.LogInfo(Tag, $"Confirmation State Queried: {state}");

            switch (state)
            {
                case TransEmp12.State.Confirmed:
                {
                    Collection.Log.LogInfo(Tag, "Verification Confirmed, Logging in Unusual Login Service...");
                    Collection.Scheduler.Cancel(QueryEvent); // cancel query task

                    if (@event.TempPassword != null) Collection.Keystore.Session.TempPassword = @event.TempPassword;
                    _unusualTask.SetResult(await DoUnusualEasyLogin());
                    break;
                }
                case TransEmp12.State.CodeExpired:
                {
                    Collection.Log.LogWarning(Tag, "Verification Expired, Please Login Again");
                    Collection.Scheduler.Cancel(QueryEvent);
                    Collection.Scheduler.Dispose();
                    
                    _unusualTask.SetResult(false);
                    break;
                }
                case TransEmp12.State.Canceled:
                {
                    Collection.Log.LogWarning(Tag, "Verification Canceled, Please Login Again");
                    Collection.Scheduler.Cancel(QueryEvent);
                    Collection.Scheduler.Dispose();
                    
                    _unusualTask.SetResult(false);
                    break;
                }
                case TransEmp12.State.WaitingForConfirm:
                default:
                    break;
            }
        }
    }

    private async Task<bool> DoUnusualEasyLogin()
    {
        Collection.Log.LogInfo(Tag, "Trying to Login by EasyLogin...");
        var unusualEvent = UnusualEasyLoginEvent.Create();
        var result = await Collection.Business.SendEvent(unusualEvent);
        return result.Count != 0 && ((UnusualEasyLoginEvent)result[0]).Success;

    }
    
    public bool SubmitCaptcha(string ticket, string randStr) => _captchaTask?.TrySetResult((ticket, randStr)) ?? false;
}