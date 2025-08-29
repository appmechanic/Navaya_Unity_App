using System;
using System.Collections;
using System.Collections.Generic;
using Unity.WebRTC;
using UnityEngine;
using Best.SignalR;
using Best.SignalR.Encoders;

public class LobbyLiveFeedUnityClient : MonoBehaviour
{
    [Header("SignalR")]
    [SerializeField] private string hubUrl = "https://your-api.example.com/lobbycontrollivefeedhub";
    [SerializeField] private int vrUserId = 19;

    [Header("Video")]
    [SerializeField] private Camera captureCamera;
    [SerializeField] private int width = 960;
    [SerializeField] private int height = 540;

    private HubConnection connection;
    private RTCPeerConnection pc;
    private VideoStreamTrack videoTrack;
    public RenderTexture renderTexture;

    private RTCRtpSender videoSender;
    private bool webrtcInitialized;

    private void Start()
    {
        StartCoroutine(WebRTC.Update());
        Debug.Log($"[VR][System] Graphics API: {SystemInfo.graphicsDeviceType}, GPU: {SystemInfo.graphicsDeviceName}, OS: {SystemInfo.operatingSystem}");
        Debug.Log($"[VR][System] Screen: {Screen.width}x{Screen.height} @ {Screen.currentResolution.refreshRateRatio}hz, Target FPS: {Application.targetFrameRate}");
        Debug.Log($"[VR][System] Supports WebCamTexture: {WebCamTexture.devices.Length} devices found.");

        try
        {
            // If you choose to explicitly initialize, uncomment and pick encoder type:
            // WebRTC.Initialize(EncoderType.Hardware);
            webrtcInitialized = true;
            Debug.Log("[VR][WebRTC] Initialized (Hardware encoder preferred).");
        }
        catch (Exception e)
        {
            Debug.LogWarning("[VR][WebRTC] Initialize failed or already initialized: " + e.Message);
        }

        StartCoroutine(StartConnection());
    }

    void Update()
    {
        if (captureCamera != null && renderTexture != null)
            captureCamera.Render();
    }

    void LateUpdate()
    {
        WebRTC.Update();
    }


    private void OnDestroy()
    {
        StopAllCoroutines();
        Cleanup();

        if (webrtcInitialized)
        {
            // WebRTC.Dispose();
            webrtcInitialized = false;
            Debug.Log("[VR][WebRTC] Disposed.");
        }
    }

    private IEnumerator StartConnection()
    {
        Debug.Log("[VR] StartConnection() begin");

        // -------- SignalR Setup --------
        var options = new HubOptions
        {
            PreferedTransport = Best.SignalR.TransportTypes.LongPolling
        };
        connection = new HubConnection(new Uri(hubUrl), new JsonProtocol(new LitJsonEncoder()), options);

        connection.OnConnected += (hub) => { Debug.Log("[VR] Connected to hub."); };
        connection.OnError += (hub, error) => { Debug.LogError("[VR] Hub error: " + error); };
        connection.OnClosed += (hub) =>
        {
            Debug.Log("[VR] Hub closed.");
            StartCoroutine(Reconnect());
        };

        connection.On<object>("ReceiveAnswer", (arg) =>
        {
            try
            {
                if (arg is string sdpStr)
                {
                    Debug.Log($"[VR] ReceiveAnswer as string, length={sdpStr?.Length}");
                    ApplyAnswer(sdpStr);
                }
                else if (arg is Dictionary<string, object> dict && dict.TryGetValue("sdp", out var sdp))
                {
                    Debug.Log($"[VR] ReceiveAnswer as dict, length={sdp?.ToString()?.Length}");
                    ApplyAnswer(sdp?.ToString());
                }
                else
                {
                    Debug.LogWarning("[VR] ReceiveAnswer got unexpected payload: " + arg?.GetType());
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[VR] ReceiveAnswer error: " + ex);
            }
        });

        connection.On<Dictionary<string, object>>("ReceiveIceCandidate", (dict) =>
        {
            try
            {
                if (dict == null)
                {
                    Debug.LogWarning("[VR] ReceiveIceCandidate: null payload.");
                    return;
                }

                var init = new RTCIceCandidateInit
                {
                    candidate = dict.TryGetValue("candidate", out var c) ? c?.ToString() : null,
                    sdpMid = dict.TryGetValue("sdpMid", out var mid) ? mid?.ToString() : null,
                    sdpMLineIndex = dict.TryGetValue("sdpMLineIndex", out var idx) && int.TryParse(idx?.ToString(), out var i) ? i : 0
                };

                Debug.Log($"[VR] Remote ICE candidate received: {init.candidate}");
                pc.AddIceCandidate(new RTCIceCandidate(init));
            }
            catch (Exception ex)
            {
                Debug.LogError("[VR] ReceiveIceCandidate error: " + ex);
            }
        });

        connection.On("ReceiveOffer", (string jsonStr) =>
        {
            Debug.Log("[VR] ReceiveOffer (ignored for broadcaster) -> " + jsonStr);
        });
        connection.On("ReadyForOffer", (string msg) =>
        {
            Debug.Log("[VR] Server ReadyForOffer -> " + msg);
        });

        // Connect Hub
        connection.StartConnect();
        yield return new WaitUntil(() => connection.State == ConnectionStates.Connected);
        Debug.Log("[VR] Hub connected.");

        connection.Send("JoinGroup", $"vruser_{vrUserId}");
        Debug.Log($"[VR] Joined group vruser_{vrUserId}");

        // -------- PeerConnection --------
        var config = new RTCConfiguration
        {
            iceServers = new[] { new RTCIceServer { urls = new[] { "stun:stun.l.google.com:19302" } } },
        };
        pc = new RTCPeerConnection(ref config);
        pc.OnConnectionStateChange = state => Debug.Log("[VR] PC state: " + state);

        pc.OnIceCandidate = ice =>
        {
            if (ice == null) return;
            var iceInit = new
            {
                candidate = ice.Candidate,
                sdpMid = ice.SdpMid,
                sdpMLineIndex = ice.SdpMLineIndex.GetValueOrDefault()
            };
            connection.Send("SendIceCandidate", vrUserId, iceInit);
            Debug.Log("[VR] Local ICE sent: " + ice.Candidate);
        };

        // -------- Camera + RenderTexture --------
        if (captureCamera == null)
        {
            Debug.LogError("[VR] No captureCamera assigned!");
            yield break;
        }

        captureCamera.enabled = true;

        if (renderTexture == null)
        {
            Debug.LogError("[VR] No RenderTexture assigned!");
            yield break;
        }

        captureCamera.targetTexture = renderTexture;
        Debug.Log($"[VR] Camera targetTexture assigned: {renderTexture.width}x{renderTexture.height}");

        // Let Unity render a few frames into the RT before creating the track
        yield return null;
        yield return new WaitForEndOfFrame();
        captureCamera.Render();
        Debug.Log("[VR] captureCamera.Render() warm-up complete.");

        if (!renderTexture.IsCreated())
        {
            renderTexture.Create();
            Debug.Log("[VR] RenderTexture was not created, now forced Create().");
        }

        Debug.Log($"[VR] RenderTexture valid={renderTexture.IsCreated()}");

        // -------- Video Track --------
        try
        {
            videoTrack = captureCamera.CaptureStreamTrack(width, height);
            videoTrack.Enabled = true;
            Debug.Log($"[VR] VideoTrack created, id={videoTrack.Id}, kind={videoTrack.Kind}");
        }
        catch (Exception ex)
        {
            Debug.LogError("[VR] Failed to create VideoStreamTrack: " + ex);
            yield break;
        }

        var stream = new MediaStream();
        stream.AddTrack(videoTrack);
        videoSender = pc.AddTrack(videoTrack, stream);
        Debug.Log($"[VR] Track added. videoSender? {videoSender != null}");

        foreach (var s in pc.GetSenders())
        {
            if (s.Track != null)
                Debug.Log($"[VR] Sender track kind={s.Track.Kind}, enabled={s.Track.Enabled}, id={s.Track.Id}");
        }

        StartOutboundStatsLogging(videoSender);
        StartCoroutine(ZeroFrameWatchdog());

        // -------- Offer SDP --------
        connection.Send("ReadyForOffer", vrUserId);
        Debug.Log("[VR] ReadyForOffer sent.");

        var offerOp = pc.CreateOffer();
        yield return offerOp;
        if (offerOp.IsError)
        {
            Debug.LogError("[VR] CreateOffer failed: " + offerOp.Error.message);
            yield break;
        }
        var offer = offerOp.Desc;

        Debug.Log($"[VR][Offer SDP]\n{offer.sdp}");
        Debug.Log($"[VR] Offer created. Contains video? {(offer.sdp.Contains("m=video") ? "YES" : "NO")}");

        var sldOp = pc.SetLocalDescription(ref offer);
        yield return sldOp;
        if (sldOp.IsError)
        {
            Debug.LogError("[VR] SetLocalDescription failed: " + sldOp.Error.message);
            yield break;
        }
        Debug.Log("[VR] LocalDescription set.");

        var offerInit = new { type = "offer", sdp = offer.sdp };
        connection.Send("SendOffer", vrUserId, offerInit);
        Debug.Log("[VR] Offer sent. Waiting for Answer/ICE...");

        Debug.Log("[VR] StartConnection() end");
    }


    private IEnumerator ZeroFrameWatchdog()
    {
        float start = Time.realtimeSinceStartup;
        int lastFrames = -1;

        while (pc != null)
        {
            yield return new WaitForSeconds(5f);

            var sender = videoSender;
            if (sender == null) continue;

            var op = sender.GetStats();
            yield return op;

            if (!op.IsError && op.Value != null)
            {
                foreach (var stat in op.Value.Stats.Values)
                {
                    if (stat.Type == RTCStatsType.OutboundRtp && stat is RTCOutboundRTPStreamStats o)
                    {
                        if (o.framesEncoded == 0 && Time.realtimeSinceStartup - start > 10f)
                        {
                            Debug.LogWarning("[VR][watchdog] framesEncoded still 0 after 10s. Checks: " +
                                             $"camera.enabled={captureCamera.enabled}, " +
                                             $"targetTexture={(captureCamera.targetTexture ? captureCamera.targetTexture.name : "null")}, " +
                                             $"rt.IsCreated={(renderTexture != null && renderTexture.IsCreated())}, " +
                                             $"pcState={pc.ConnectionState}. " +
                                             "If remote is Safari/iOS try VP8 only; also ensure WebRTC.Update() runs every frame.");
                        }
                        if (o.framesEncoded != lastFrames)
                            lastFrames = (int)o.framesEncoded;
                    }
                }
            }
        }
    }

    private void StartOutboundStatsLogging(RTCRtpSender sender)
    {
        StartCoroutine(OutboundStatsCoroutine(sender));
    }

    private IEnumerator OutboundStatsCoroutine(RTCRtpSender sender)
    {
        while (sender != null && pc != null)
        {
            var op = sender.GetStats();
            yield return op;
            if (!op.IsError && op.Value != null)
            {
                foreach (var stat in op.Value.Stats.Values)
                {
                    if (stat.Type == RTCStatsType.OutboundRtp && stat is RTCOutboundRTPStreamStats o)
                    {
                        Debug.Log($"[VR][stats] outbound-rtp: frames={o.framesEncoded}, bytes={o.bytesSent}, fps={o.framesPerSecond}, keyFrames={o.keyFramesEncoded}");
                    }
                }
            }
            else if (op.IsError)
            {
                Debug.LogWarning("[VR] stats error: " + op.Error.message);
            }
            yield return new WaitForSeconds(3f);
        }
    }

    private void ApplyAnswer(string sdp)
    {
        if (string.IsNullOrEmpty(sdp))
        {
            Debug.LogError("[VR] ApplyAnswer failed: empty SDP.");
            return;
        }

        // 🔎 Log the raw SDP
        Debug.Log($"[VR][Answer SDP]\n{sdp}");

        // 🔎 Quick check if video line exists
        Debug.Log("[VR] RemoteDescription video line? " +
                  (sdp.Contains("m=video") ? "YES" : "NO"));

        var desc = new RTCSessionDescription { type = RTCSdpType.Answer, sdp = sdp };
        var op = pc.SetRemoteDescription(ref desc);
        StartCoroutine(WaitForOp(op, "SetRemoteDescription(Answer)"));
    }

    private IEnumerator WaitForOp(RTCSetSessionDescriptionAsyncOperation op, string tag)
    {
        yield return op;
        if (op.IsError)
            Debug.LogError($"[VR] {tag} failed: {op.Error.message}");
        else
            Debug.Log($"[VR] {tag} success.");
    }

    private void Cleanup()
    {
        try
        {
            if (connection != null && connection.State == ConnectionStates.Connected)
            {
                try { connection.Send("LeaveGroup", $"vruser_{vrUserId}"); } catch { }
            }
        }
        finally
        {
            try
            {
                if (videoTrack != null)
                {
                    videoTrack.Enabled = false;
                    videoTrack.Dispose();
                    videoTrack = null;
                }

                if (renderTexture != null)
                {
                    captureCamera.targetTexture = null;
                    if (renderTexture.IsCreated()) renderTexture.Release();
                    Destroy(renderTexture);
                    renderTexture = null;
                }

                if (pc != null)
                {
                    pc.Close();
                    pc.Dispose();
                    pc = null;
                }

                if (connection != null)
                {
                    try { connection.StartClose(); } catch { }
                    connection = null;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[VR] Cleanup exception: " + e.Message);
            }
        }
    }

    private IEnumerator Reconnect()
    {
        Debug.Log("[VR] Attempting reconnect in 5 seconds...");
        yield return new WaitForSeconds(5);
        Cleanup();
        yield return StartConnection();
    }
}