using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Android;
using TMPro;
using System.IO;
using RenderHeads.Media.AVProVideo;
using UnityEngine.UI;
using Best.SignalR;
using Best.SignalR.Encoders;
using Unity.WebRTC;

public class HeadsetSessionManager : MonoBehaviour
{

    private const string baseUrl = "https://navaya-api-atajgwazdnfmeac5.ukwest-01.azurewebsites.net";
    private string deviceId;
    private int vrUserId;

    private HubConnection mediaHubConnection;

    public List<string> imageBlobUrls = new List<string>();
    public List<string> videoBlobUrls = new List<string>();

    public List<GameObject> thumbnailPlanes;
    public MediaPlayer mediaPlayer;
    private Queue<(string path, GameObject plane)> pendingVideoThumbnails = new();
    private bool isProcessingThumbnail = false;

    private int thumbnailIndex = 0;
    public SessionOverlayUI sessionOverlay;
    public GameObject startupSphere;
    public GameObject videoSphere;
    public GameObject display2DQuad;
    public GameObject startupCanvasGroup;
    public TMP_Text statusText;
    public TMP_Text statusTextTwo;
    public Slider progressBar;

    public GameObject environmentRoot;
    public GameObject overlayCanvas;
    private bool isThumbnailMode = false;
    [SerializeField] private RawImage targetImage;
    private string sessionType;
    private readonly Queue<Action> mainThreadQueue = new Queue<Action>();
    [HideInInspector] public bool is3DVideo = false;

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

    [Serializable]
    public class VrStartupConfig
    {
        public int vrUserId;
        public string sessionType;
    }

    [System.Serializable]
    public class SasUrlResponse
    {
        public string sasUrl;
    }

    [System.Serializable]
    public class MediaItem
    {
        public string fileName;
        public string mediaType;
        public string category;
    }


    [Serializable]
    public class HeadsetStatus
    {
        public string headsetName;
        public string wifiName;
        public string batteryLevel;
    }

    private void Awake()
    {
        mediaPlayer.Events.AddListener(OnMediaPlayerEvent);
    }

    void Start()
    {
        StartCoroutine(WebRTC.Update());

        try
        {
            webrtcInitialized = true;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[VR][WebRTC] Initialize failed or already initialized: " + e.Message);
        }

        StartCoroutine(FullStartupSequence());
        StartCoroutine(UploadHeadsetInfoPeriodically());
    }
    void Update()
    {
        lock (mainThreadQueue)
        {
            while (mainThreadQueue.Count > 0)
                mainThreadQueue.Dequeue()?.Invoke();
        }
        if (captureCamera != null && renderTexture != null)
            captureCamera.Render();
    }

    void LateUpdate()
    {
        WebRTC.Update();
    }
    private void OnMediaPlayerEvent(MediaPlayer mp, MediaPlayerEvent.EventType evtType, ErrorCode error)
    {
        switch (evtType)
        {
            case MediaPlayerEvent.EventType.ReadyToPlay:
                if (!isProcessingThumbnail)
                    StartCoroutine(ExtractVideoThumbnailAtTime(2.0f));
                break;

            case MediaPlayerEvent.EventType.FirstFrameReady:
                if (!isThumbnailMode)
                {
                    if (is3DVideo)
                    {
                        display2DQuad.SetActive(false);
                        videoSphere.SetActive(true);
                        environmentRoot.SetActive(false);
                    }
                    else
                    {
                        display2DQuad.SetActive(true);
                        videoSphere.SetActive(false);
                        environmentRoot.SetActive(true);
                    }
                    overlayCanvas.SetActive(false);
                }
                break;

            case MediaPlayerEvent.EventType.FinishedPlaying:
            case MediaPlayerEvent.EventType.Closing:
                if (!isThumbnailMode)
                {
                    videoSphere.SetActive(false);
                    display2DQuad.SetActive(false);
                    environmentRoot.SetActive(true);
                   // overlayCanvas.SetActive(true);
                   // sessionOverlay.ShowOverlay("startSession");
                }
                break;
        }
    }



    IEnumerator FullStartupSequence()
    {
        startupSphere.SetActive(true);
        environmentRoot.SetActive(false);
      //  overlayCanvas.SetActive(false);
        startupCanvasGroup.gameObject.SetActive(true);
        progressBar.value = 0;

        UpdateStatus("Requesting permissions...");
        if (!Permission.HasUserAuthorizedPermission(Permission.FineLocation))
        {
            Permission.RequestUserPermission(Permission.FineLocation);
            yield return new WaitForSeconds(1f);
        }

        UpdateStatus("Initializing device info...");
        yield return new WaitForSeconds(1f);

        deviceId = GetDeviceId();
        string wifi = GetWifiSSID();
        string battery = Mathf.RoundToInt(SystemInfo.batteryLevel * 100).ToString();

        UpdateStatus("Fetching session config...");
        yield return GetVrStartupConfig(deviceId);
        progressBar.value = 0.25f;

        UpdateStatus("Downloading media list...");
        yield return GetMediaForVrUser(vrUserId);
        progressBar.value = 0.5f;

        UpdateStatus("Generating thumbnails...");
        yield return StartCoroutine(GenerateAllThumbnails());
        progressBar.value = 0.75f;

        UpdateStatus("Connecting to SignalR...");
        yield return UploadHeadsetInfo(deviceId, wifi, battery);
        yield return ConnectToSignalR_MediaHub(deviceId);
        progressBar.value = 1.0f;
        yield return StartConnection();
        UpdateStatus("Startup complete. Loading environment...");
        yield return new WaitForSeconds(0.5f);

        startupSphere.SetActive(false);
        startupCanvasGroup.gameObject.SetActive(false);
        environmentRoot.SetActive(true);
    }

    void UpdateStatus(string message)
    {
        if (statusText != null)
            statusText.text = message;

        Debug.Log("[Startup] " + message);
    }

    void TestStatus(string message)
    {
        if (statusTextTwo != null)
            statusTextTwo.text = message;

        Debug.Log("[Startup] " + message);
    }

    IEnumerator GetVrStartupConfig(string headsetName)
    {
        string url = $"{baseUrl}/api/MediaManagement/GetVrStartupConfig/{headsetName}";
        using UnityWebRequest req = UnityWebRequest.Get(url);
        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success)
        {
            VrStartupConfig config = JsonUtility.FromJson<VrStartupConfig>(req.downloadHandler.text);
            vrUserId = config.vrUserId;
            sessionType = config.sessionType;

            Debug.Log($"VR User ID: {vrUserId} | Session Type: {sessionType}");
            TestStatus($"Device ID: {deviceId}\nWiFi: {GetWifiSSID()}\nBattery: {Mathf.RoundToInt(SystemInfo.batteryLevel * 100)}%\nVR User ID: {vrUserId}\nSession: {sessionType}");
        }
        else
        {
            Debug.LogError("Failed to get VR startup config: " + req.error);
            TestStatus("Failed to retrieve session config.");
        }
    }

    IEnumerator GetMediaForVrUser(int userId)
    {
        string url = $"{baseUrl}/api/MediaManagement/GetMediaForVrUser/{userId}";
        using UnityWebRequest req = UnityWebRequest.Get(url);
        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success)
        {
            string json = req.downloadHandler.text;
            MediaItem[] mediaItems = JsonHelper.FromJsonArray<MediaItem>(json);

            int total = mediaItems.Length;
            int completed = 0;

            foreach (var item in mediaItems)
            {
                string blobPath = $"library/{item.mediaType.ToLower()}/{item.category.ToLower().Replace(" ", "_")}/{item.fileName}";
                yield return StartCoroutine(GetSasUrl(blobPath, item.mediaType));

                completed++;
                float globalProgress = (float)completed / total;
                progressBar.value = 0.25f + globalProgress * 0.25f;
            }
        }
    }

    IEnumerator GetSasUrl(string blobPath, string mediaType)
    {
        string url = $"{baseUrl}/api/MediaManagement/GenerateSasToken/{blobPath}";
        Debug.Log($"Requesting SAS URL: {url}");

        string fileName = Path.GetFileName(blobPath);
        string localPath = Path.Combine(Application.persistentDataPath, "Media", fileName);

        if (File.Exists(localPath))
        {
            Debug.Log($"File already exists locally: {localPath}");

            if (mediaType.Equals("Images", StringComparison.OrdinalIgnoreCase))
            {
                imageBlobUrls.Add(localPath);
            }
            else if (mediaType.Equals("Videos", StringComparison.OrdinalIgnoreCase))
            {
                videoBlobUrls.Add(localPath);
            }

            yield break;
        }

        using UnityWebRequest req = UnityWebRequest.Get(url);
        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success)
        {
            SasUrlResponse response = JsonUtility.FromJson<SasUrlResponse>(req.downloadHandler.text);
            string sasUrl = response.sasUrl;
            Debug.Log($"Received SAS URL: {sasUrl}");

            yield return StartCoroutine(DownloadMediaToLocal(sasUrl, fileName,
    downloadedPath =>
    {
        if (!string.IsNullOrEmpty(downloadedPath))
        {
            if (mediaType.Equals("Images", StringComparison.OrdinalIgnoreCase))
                imageBlobUrls.Add(downloadedPath);
            else if (mediaType.Equals("Videos", StringComparison.OrdinalIgnoreCase))
                videoBlobUrls.Add(downloadedPath);
        }
        else
        {
            Debug.LogError($"Failed to save {mediaType} locally: {sasUrl}");
        }
    },
    progress => UpdateDownloadProgressUI(fileName, progress)
));
        }
        else
        {
            Debug.LogError("Failed to get SAS URL: " + req.error);
        }
    }

    private IEnumerator DownloadMediaToLocal(string sasUrl, string fileName, Action<string> onComplete, Action<float> onProgress = null)
    {
        Uri uri = new Uri(sasUrl);
        bool is360Video = uri.AbsolutePath.Contains("/videos/360/", StringComparison.OrdinalIgnoreCase);
        string basePath = uri.AbsolutePath.Substring(0, uri.AbsolutePath.LastIndexOf('/') + 1);
        string encodedFileName = Uri.EscapeDataString(fileName);
        string finalUrl = $"{uri.Scheme}://{uri.Host}{basePath}{encodedFileName}{uri.Query}";

        string folder = Path.Combine(Application.persistentDataPath, "Media");
        if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
        string localFileName = fileName;
        if (is360Video)
        {
            string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);
            localFileName = $"{nameWithoutExt}_360{ext}";
        }
        string localPath = Path.Combine(folder, localFileName);

        if (File.Exists(localPath))
        {
            onComplete?.Invoke(localPath);
            yield break;
        }

        using (UnityWebRequest uwr = UnityWebRequest.Get(finalUrl))
        {
            uwr.timeout = 0;
            uwr.downloadHandler = new DownloadHandlerFile(localPath);
            uwr.SendWebRequest();

            while (!uwr.isDone)
            {
                float progress = uwr.downloadProgress;
                onProgress?.Invoke(progress);
                yield return null;
            }

            if (uwr.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"Download failed for {localFileName}: {uwr.error}");
                onComplete?.Invoke(null);
            }
            else
            {
                onComplete?.Invoke(localPath);
            }
        }
    }


    private void UpdateDownloadProgressUI(string fileName, float progress)
    {
        if (statusText != null)
            statusText.text = $"Downloading: {fileName} ({Mathf.RoundToInt(progress * 100)}%)";

        if (progressBar != null)
            progressBar.value = Mathf.Clamp01(progress);
    }

    private IEnumerator GenerateAllThumbnails()
    {
        List<(string path, string mediaType)> allMedia = new();

        foreach (var path in imageBlobUrls)
            allMedia.Add((path, "Images"));

        foreach (var path in videoBlobUrls)
            allMedia.Add((path, "Videos"));

        foreach (var (path, type) in allMedia)
        {
            yield return StartCoroutine(GenerateThumbnail(path, type));
        }
    }

    private IEnumerator GenerateThumbnail(string localPath, string mediaType)
    {
        string fileName = Path.GetFileNameWithoutExtension(localPath);

       /* if (mediaType.Equals("Images", StringComparison.OrdinalIgnoreCase) &&
            fileName.ToLower().Contains("navaya"))
        {
            Debug.Log($"Skipping image: {fileName}");
            yield break;
        }*/

        if (thumbnailIndex >= thumbnailPlanes.Count)
            yield break;

        GameObject targetPlane = thumbnailPlanes[thumbnailIndex];

        if (mediaType.Equals("Images", StringComparison.OrdinalIgnoreCase))
        {
            byte[] imageData = File.ReadAllBytes(localPath);
            Texture2D tex = new Texture2D(2, 2);
            tex.LoadImage(imageData);

            ApplyTextureToPlane(targetPlane, tex);
            thumbnailIndex++;
        }
        else if (mediaType.Equals("Videos", StringComparison.OrdinalIgnoreCase))
        {
            pendingVideoThumbnails.Enqueue((localPath, targetPlane));
            TryProcessNextThumbnail();
            thumbnailIndex++;
        }

        yield return null;
    }

    private void ApplyTextureToPlane(GameObject plane, Texture2D texture)
    {
        if (plane == null || texture == null) return;

        Renderer renderer = plane.GetComponent<Renderer>();
        if (renderer != null)
        {
            renderer.material = new Material(renderer.material);
            renderer.material.mainTexture = texture;
        }
    }

    private IEnumerator ExtractVideoThumbnailAtTime(double time)
    {
        if (pendingVideoThumbnails.Count == 0)
            yield break;

        isProcessingThumbnail = true;

        var (videoPath, plane) = pendingVideoThumbnails.Dequeue();
        Debug.Log($"Opening video for thumbnail: {videoPath}");

        isThumbnailMode = true;
        mediaPlayer.OpenMedia(MediaPathType.AbsolutePathOrURL, videoPath, autoPlay: false);
        yield return new WaitUntil(() => mediaPlayer.Info != null && mediaPlayer.Control.CanPlay());

        double targetSeekTime = time;
        mediaPlayer.Control.Seek(targetSeekTime);

        yield return new WaitUntil(() =>
            mediaPlayer.Info != null &&
            Math.Abs(mediaPlayer.Control.GetCurrentTime() - targetSeekTime) < 0.1
        );

        int width = mediaPlayer.Info.GetVideoWidth();
        int height = mediaPlayer.Info.GetVideoHeight();

        if (width <= 16 || height <= 16)
        {
            Debug.LogWarning("Invalid video resolution for thumbnail.");
            mediaPlayer.CloseMedia();
            isProcessingThumbnail = false;
            TryProcessNextThumbnail();
            yield break;
        }

        Texture2D targetTexture = new Texture2D(width, height, TextureFormat.BGRA32, false);
        targetTexture.name = $"Thumbnail_{Path.GetFileName(videoPath)}_{time}s";

        mediaPlayer.ExtractFrameAsync(targetTexture, (Texture2D extractedFrame) =>
        {
            if (extractedFrame != null && plane != null)
            {
                ApplyTextureToPlane(plane, extractedFrame);
            }

            mediaPlayer.CloseMedia();
            isProcessingThumbnail = false;
            TryProcessNextThumbnail();
        }, targetSeekTime, true);
    }


    private void TryProcessNextThumbnail()
    {
        if (isProcessingThumbnail || pendingVideoThumbnails.Count == 0)
            return;

        StartCoroutine(ExtractVideoThumbnailAtTime(2.0));
    }

    IEnumerator UploadHeadsetInfo(string id, string wifi, string battery)
    {
        string url = $"{baseUrl}/api/MediaManagement/UpdateHeadsetStatus";

        var payload = new HeadsetStatus
        {
            headsetName = id,
            wifiName = wifi,
            batteryLevel = battery
        };

        string json = JsonUtility.ToJson(payload);
        using UnityWebRequest req = UnityWebRequest.Put(url, json);
        req.method = UnityWebRequest.kHttpVerbPOST;
        req.SetRequestHeader("Content-Type", "application/json");

        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success)
            Debug.Log("Headset info uploaded.");
        else
            Debug.LogError("Upload failed: " + req.error);
    }

    IEnumerator ConnectToSignalR_MediaHub(string headsetName)
    {
        string hubUrl = $"{baseUrl}/lobbymediahub?headsetName={headsetName}";

        HubOptions options = new HubOptions
        {
            PreferedTransport = Best.SignalR.TransportTypes.LongPolling
        };

        mediaHubConnection = new HubConnection(new Uri(hubUrl), new JsonProtocol(new LitJsonEncoder()), options);

        mediaHubConnection.OnConnected += (hub) =>
        {
            Debug.Log("[SignalR]  Connected to hub.");
        };

        mediaHubConnection.OnError += (hub, error) =>
        {
            Debug.LogError("[SignalR]  Error: " + error);
        };

        mediaHubConnection.OnClosed += (hub) =>
        {
            Debug.Log("[SignalR]  Connection closed.");
            StartCoroutine(ReconnectSignalR());
        };

        mediaHubConnection.On<string, Dictionary<string, object>>("ReceiveMediaCommand", (command, dataDict) =>
        {
            lock (mainThreadQueue)
            {
                mainThreadQueue.Enqueue(() =>
                {
                    Debug.Log($"[SignalR] ReceivedMediaCommand -> {command}");
                    HandleSignalRCommand(command, dataDict);
                });
            }
        });

        mediaHubConnection.StartConnect();
        yield return new WaitUntil(() => mediaHubConnection.State == ConnectionStates.Connected);
        Debug.Log(" Lobby SignalR Connected");

        yield return WaitForTask(mediaHubConnection.InvokeAsync<object>("JoinGroup", $"vruser_{vrUserId}"));
        Debug.Log($" Joined SignalR group: vruser_{vrUserId}");
    }

    private void HandleSignalRCommand(string command, Dictionary<string, object> dataDict)
    {
        switch (command)
        {
            case "startSession":
                sessionOverlay.ShowOverlay("startSession");
                break;

            case "endSession":
                mediaPlayer.Control.Pause();
                videoSphere.SetActive(false);
                environmentRoot.SetActive(true);
                targetImage.gameObject.SetActive(false);
                overlayCanvas.SetActive(true);
                sessionOverlay.ShowOverlay("endSession");
                break;

            case "play":
            case "next":
            case "back":
                string mediaType = dataDict.TryGetValue("mediaType", out var mt) ? mt?.ToString() : null;
                int index = dataDict.TryGetValue("index", out var idx) ? Convert.ToInt32(idx) : -1;

                if (mediaType == "video" && index >= 0 && index < videoBlobUrls.Count)
                {
                    targetImage.gameObject.SetActive(false);
                    PlayVideo(index);
                }
                else if (mediaType == "photo" && index >= 0 && index < imageBlobUrls.Count)
                {
                    mediaPlayer.Control.Pause();
                    videoSphere.SetActive(false);
                    display2DQuad.SetActive(false);
                    environmentRoot.SetActive(true);
                    targetImage.gameObject.SetActive(true);
                    ApplyImage(imageBlobUrls[index]);
                }
                break;

            case "pause":
                mediaPlayer.Control.Pause();
                break;

            case "resumeMedia":
                mediaPlayer.Control.Play();
                break;

            case "setVolume":
                if (dataDict.TryGetValue("volume", out var vol))
                    SetPlayerVolume(Convert.ToDouble(vol));
                break;

            case "hideOverlay":
                sessionOverlay.HideOverlay();
                break;

            case "reloadContent":
                StartCoroutine(ReloadAllContent());
                break;

            case "shutdown":
                Application.Quit();
                break;

            default:
                Debug.LogWarning($"Unknown SignalR command: {command}");
                break;
        }
    }

    private IEnumerator ReconnectSignalR()
    {
        yield return new WaitForSeconds(2);
        mediaHubConnection.StartConnect();
    }

    private IEnumerator ReloadAllContent()
    {
        imageBlobUrls.Clear();
        videoBlobUrls.Clear();
        thumbnailIndex = 0;
        pendingVideoThumbnails.Clear();

        foreach (var plane in thumbnailPlanes)
        {
            ApplyTextureToPlane(plane, Texture2D.blackTexture);
        }

        yield return GetMediaForVrUser(vrUserId);
        yield return GenerateAllThumbnails();
    }

    private IEnumerator StartConnection()
    {
        Debug.Log("[VR] StartConnection() begin");
        string hubUrl = $"{baseUrl}/lobbycontrollivefeedhub";
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

        connection.StartConnect();
        yield return new WaitUntil(() => connection.State == ConnectionStates.Connected);
        Debug.Log("[VR] Hub connected.");

        connection.Send("JoinGroup", $"vruser_{vrUserId}");
        Debug.Log($"[VR] Joined group vruser_{vrUserId}");

        /*  var config = new RTCConfiguration
          {
              iceServers = new[] { new RTCIceServer { urls = new[] { "stun:stun.l.google.com:19302" } } },
          };*/
        var config = new RTCConfiguration
        {
            iceServers = new[]
            {
        // STUN servers
        new RTCIceServer { urls = new[] { "stun:stun.l.google.com:19302" } },
        new RTCIceServer { urls = new[] { "stun:stun1.l.google.com:19302" } },

        // TURN server from ExpressTURN
        new RTCIceServer
        {
            urls = new[] { "turn:relay1.expressturn.com:3480?transport=tcp" },
            username = "000000002071726250",
            credential = "KP+b6An/aVXPo26p1Ae5jZyZoq0="
        }
    },
            iceTransportPolicy = RTCIceTransportPolicy.All,
            bundlePolicy = RTCBundlePolicy.BundlePolicyMaxBundle
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

        try
        {
            videoTrack = new VideoStreamTrack(renderTexture);
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

        Debug.Log($"[VR][Answer SDP]\n{sdp}");

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

    private async Task CleanupAsync_BestSignalR()
    {
        try
        {
            if (connection != null && connection.State == ConnectionStates.Connected)
            {
                await Task.Run(() => {
                    try { connection.Send("LeaveGroup", $"vruser_{vrUserId}"); } catch { }
                });
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

                if (captureCamera != null && renderTexture != null && captureCamera.targetTexture == renderTexture)
                    captureCamera.targetTexture = null;

                renderTexture?.Release();
                Destroy(renderTexture);
                renderTexture = null;
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
        _ = CleanupAsync_BestSignalR();
        yield return StartConnection();
    }

    public void PlayVideo(int index)
    {
        if (index < 0 || index >= videoBlobUrls.Count)
        {
            Debug.LogError($"PlayVideo: Invalid index {index}. Total videos: {videoBlobUrls.Count}");
            return;
        }

        string videoPath = videoBlobUrls[index];
        string fileName = Path.GetFileNameWithoutExtension(videoPath);

        is3DVideo = fileName.EndsWith("_360", StringComparison.OrdinalIgnoreCase);

        Debug.Log($"PlayVideo -> Path: {videoPath} 3D: {is3DVideo}");

        if (targetImage != null)
            targetImage.gameObject.SetActive(false);

      /*  if (is3DVideo)
        {
            
            environmentRoot.SetActive(false);
            videoSphere.SetActive(true);
        }
        else
        {
            videoSphere.SetActive(false);
            environmentRoot.SetActive(true);
            display2DQuad.SetActive(true);
        }*/
        PlayMediaFromPath(videoPath, resetTime: true);
    }

    private void PlayMediaFromPath(string path, bool resetTime = true)
    {
        isThumbnailMode = false;

        mediaPlayer.OpenMedia(MediaPathType.AbsolutePathOrURL, path, autoPlay: true);
        if (resetTime)
            mediaPlayer.Control.Seek(0.0f);
    }

    public void ApplyImage(string imagePath)
    {
        if (string.IsNullOrEmpty(imagePath))
        {
            Debug.LogWarning("ApplyImage: imagePath is null or empty.");
            return;
        }

        if (!File.Exists(imagePath))
        {
            Debug.LogWarning($"ApplyImage: File does not exist at path: {imagePath}");
            return;
        }

        try
        {
            byte[] imageBytes = File.ReadAllBytes(imagePath);
            Texture2D texture = new Texture2D(2, 2);
            if (texture.LoadImage(imageBytes))
            {
                if (targetImage != null)
                {
                    targetImage.texture = texture;
                    targetImage.enabled = true;
                    targetImage.SetNativeSize();
                    Debug.Log($"ApplyImage: Successfully applied image from path: {imagePath}");
                }
                else
                {
                    Debug.LogWarning("ApplyImage: targetImage is not assigned.");
                }
            }
            else
            {
                Debug.LogWarning("ApplyImage: Failed to load image data into texture.");
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"ApplyImage: Exception occurred while loading image: {ex.Message}");
        }
    }

    void SetPlayerVolume(double volume)
    {
        mediaPlayer.AudioVolume = (float)Mathf.Clamp01((float)volume);
    }

    private IEnumerator UploadHeadsetInfoPeriodically()
    {
        while (true)
        {
            string wifi = GetWifiSSID();
            string battery = Mathf.RoundToInt(SystemInfo.batteryLevel * 100).ToString();
            yield return UploadHeadsetInfo(deviceId, wifi, battery);

            yield return new WaitForSeconds(60);
        }
    }

    IEnumerator WaitForTask(Task task, float timeoutSeconds = 10f)
    {
        float start = Time.time;
        while (!task.IsCompleted && Time.time - start < timeoutSeconds)
            yield return null;

        if (!task.IsCompleted)
            Debug.LogError("Task timed out.");
        else if (task.IsFaulted)
            Debug.LogError("Task error: " + task.Exception?.GetBaseException().Message);
    }

    private void OnDestroy()
    {
        StopAllCoroutines();
        _ = CleanupAsync_BestSignalR();

        if (webrtcInitialized)
        {
            webrtcInitialized = false;
            Debug.Log("[VR][WebRTC] Disposed.");
        }
    }

    string GetDeviceId()
    {
#if UNITY_ANDROID && !UNITY_EDITOR && PICO_PLATFORM
    try
    {
        return Unity.XR.PXR.PXR_Plugin.System.UPxr_GetDeviceSN();
    }
    catch (System.Exception ex)
    {
        Debug.LogWarning($"[DeviceID] Failed to get PICO device serial number: {ex.Message}. Using fallback.");
        return "2GOYC1ZF9ZO1G8";
    }
#elif UNITY_ANDROID && !UNITY_EDITOR
    // If running on Android without PICO SDK
        return SystemInfo.deviceUniqueIdentifier;
#else
        return "2GOYC1ZF9ZO1G8";
#endif
    }

    string GetWifiSSID()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
            var pluginClass = new AndroidJavaClass("com.example.wifidetector.WifiInfoPlugin");
            return pluginClass.CallStatic<string>("getWifiName", activity);
        }
        catch
        {
            return "Unavailable";
        }
#else
        return "EditorWiFi";
#endif
    }
}

public static class JsonHelper
{
    public static T[] FromJsonArray<T>(string json)
    {
        string newJson = "{\"array\":" + json + "}";
        Wrapper<T> wrapper = JsonUtility.FromJson<Wrapper<T>>(newJson);
        return wrapper.array;
    }

    [System.Serializable]
    private class Wrapper<T>
    {
        public T[] array;
    }
}
