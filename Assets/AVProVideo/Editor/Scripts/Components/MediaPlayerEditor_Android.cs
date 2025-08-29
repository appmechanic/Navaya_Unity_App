using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

//-----------------------------------------------------------------------------
// Copyright 2015-2025 RenderHeads Ltd.  All rights reserved.
//-----------------------------------------------------------------------------

namespace RenderHeads.Media.AVProVideo.Editor
{
	/// <summary>
	/// Editor for the MediaPlayer component
	/// </summary>
	public partial class MediaPlayerEditor : UnityEditor.Editor
	{
		private readonly static GUIContent[] _audioModesAndroid =
		{
			new GUIContent("System Direct"),
			new GUIContent("Unity"),
			new GUIContent("Facebook Audio 360", "Initialises player with Facebook Audio 360 support"),
		};

		private readonly static GUIContent[] _blitTextureFilteringAndroid =
		{
			new GUIContent("Point"),
			new GUIContent("Bilinear"),
			new GUIContent("Trilinear"),
		};

		private readonly static FieldDescription _optionFileOffset = new FieldDescription(".fileOffset", GUIContent.none);
		private readonly static FieldDescription _optionGenerateMipmaps = new FieldDescription("._generateMipmaps", new GUIContent("Generate Mipmaps", "Generate a complete mipmap chain for the output texture. Not supported when the texture format is set to OES"));

//		private readonly static FieldDescription _optionBlitTextureFiltering = new FieldDescription(".blitTextureFiltering", new GUIContent("Blit Texture Filtering", "The texture filtering used for the final internal blit."));
//		private readonly static FieldDescription _optionShowPosterFrames = new FieldDescription(".showPosterFrame", new GUIContent("Show Poster Frame", "Allows a paused loaded video to display the initial frame. This uses up decoder resources."));
		private readonly static FieldDescription _optionPreferSoftwareDecoder = new FieldDescription(".preferSoftwareDecoder", GUIContent.none);
		private readonly static FieldDescription _optionForceRtpTCP = new FieldDescription(".forceRtpTCP", GUIContent.none);
		private readonly static FieldDescription _optionForceEnableMediaCodecAsynchronousQueueing = new FieldDescription(".forceEnableMediaCodecAsynchronousQueueing", GUIContent.none);
		private readonly static FieldDescription _optionPreferredMaximumResolution = new FieldDescription("._preferredMaximumResolution", new GUIContent("Preferred Maximum Resolution", "The desired maximum resolution of the video."));
#if UNITY_2017_2_OR_NEWER
		private readonly static FieldDescription _optionCustomPreferredMaxResolution = new FieldDescription("._customPreferredMaximumResolution", new GUIContent(" "));
#endif
		private readonly static FieldDescription _optionCustomPreferredPeakBitRate = new FieldDescription("._preferredPeakBitRate", new GUIContent("Preferred Peak BitRate", "The desired limit of network bandwidth consumption for playback, set to 0 for no preference."));
		private readonly static FieldDescription _optionCustomPreferredPeakBitRateUnits = new FieldDescription("._preferredPeakBitRateUnits", new GUIContent());

		private readonly static FieldDescription _optionMinBufferMs = new FieldDescription(".minBufferMs", new GUIContent("Minimum Buffer Ms"));
		private readonly static FieldDescription _optionMaxBufferMs = new FieldDescription(".maxBufferMs", new GUIContent("Maximum Buffer Ms"));
		private readonly static FieldDescription _optionBufferForPlaybackMs = new FieldDescription(".bufferForPlaybackMs", new GUIContent("Buffer For Playback Ms"));
		private readonly static FieldDescription _optionBufferForPlaybackAfterRebufferMs = new FieldDescription(".bufferForPlaybackAfterRebufferMs", new GUIContent("Buffer For Playback After Rebuffer Ms"));

		private void OnInspectorGUI_Override_Android()
		{
			//MediaPlayer media = (this.target) as MediaPlayer;
			//MediaPlayer.OptionsAndroid options = media._optionsAndroid;

			GUILayout.Space(8f);

			string optionsVarName = MediaPlayer.GetPlatformOptionsVariable(Platform.Android);

			{
				EditorGUILayout.BeginVertical(GUI.skin.box);

				DisplayPlatformOption(optionsVarName, _optionVideoAPI);

				SerializedProperty propVideoOutputMode = DisplayPlatformOption(optionsVarName, _optionVideoOutputMode);
				if (propVideoOutputMode.enumValueIndex == (int)MediaPlayer.OptionsAndroid.VideoOutputMode.Texture)
				{
					SerializedProperty propTextureFormat = DisplayPlatformOption(optionsVarName, _optionTextureFormat);
					bool isOES = propTextureFormat.enumValueIndex == (int)MediaPlayer.PlatformOptions.TextureFormat.YCbCr420_OES;
					if (isOES)
					{
						EditorHelper.IMGUI.NoticeBox(MessageType.Info, "The OES texture format is only supported when using the OpenGL ES3 renderer, and requires special shaders.  Make sure to assign an AVPro Video OES shader type to the meshes or materials that need to display video.");
						EditorHelper.IMGUI.NoticeBox(MessageType.Warning, "The OES texture format is not supported when using the Vulkan renderer or with the trial version of the plugin.");
					}

					// Generate mipmaps - only non-OES
					if (!isOES)
					{
						SerializedProperty propGenerateMipmaps = DisplayPlatformOption(optionsVarName, _optionGenerateMipmaps);
					}
				}

				{
					SerializedProperty propFileOffset = DisplayPlatformOption(optionsVarName, _optionFileOffset);
					propFileOffset.intValue = Mathf.Max(0, propFileOffset.intValue);
				}

				EditorGUILayout.EndVertical();
			}

			if (_showUltraOptions)
			{
				SerializedProperty httpHeadersProp = serializedObject.FindProperty(optionsVarName + ".httpHeaders.httpHeaders");
				if (httpHeadersProp != null)
				{
					OnInspectorGUI_HttpHeaders(httpHeadersProp);
				}

				SerializedProperty keyAuthProp = serializedObject.FindProperty(optionsVarName + ".keyAuth");
				if (keyAuthProp != null)
				{
					OnInspectorGUI_HlsDecryption(keyAuthProp);
				}
			}

#if false
			// MediaPlayer API options
			{
				EditorGUILayout.BeginVertical(GUI.skin.box);
				GUILayout.Label("MediaPlayer API Options", EditorStyles.boldLabel);

				DisplayPlatformOption(optionsVarName, _optionShowPosterFrames);

				EditorGUILayout.EndVertical();
			}
#endif

			// ExoPlayer API options
			{
				EditorGUILayout.BeginVertical(GUI.skin.box);
				GUILayout.Label("ExoPlayer API Options", EditorStyles.boldLabel);

				DisplayPlatformOption(optionsVarName, _optionPreferSoftwareDecoder);
				DisplayPlatformOption(optionsVarName, _optionForceRtpTCP);
				DisplayPlatformOption(optionsVarName, _optionForceEnableMediaCodecAsynchronousQueueing);

				// Audio
				{
					SerializedProperty propAudioOutput = DisplayPlatformOptionEnum(optionsVarName, _optionAudioOutput, _audioModesAndroid);
					if ((Android.AudioOutput)propAudioOutput.enumValueIndex == Android.AudioOutput.FacebookAudio360)
					{
						if (_showUltraOptions)
						{
							EditorGUILayout.Space();
							EditorGUILayout.LabelField("Facebook Audio 360", EditorStyles.boldLabel);
							DisplayPlatformOptionEnum(optionsVarName, _optionAudio360ChannelMode, _audio360ChannelMapGuiNames);
							DisplayPlatformOption(optionsVarName, _optionAudio360LatencyMS);
						}
					}
				}

				GUILayout.Space(8f);

//				EditorGUILayout.BeginVertical();
				EditorGUILayout.LabelField("Adaptive Stream", EditorStyles.boldLabel);

				DisplayPlatformOption(optionsVarName, _optionStartMaxBitrate);

				{
					SerializedProperty preferredMaximumResolutionProp = DisplayPlatformOption(optionsVarName, _optionPreferredMaximumResolution);
					if ((MediaPlayer.OptionsAndroid.Resolution)preferredMaximumResolutionProp.intValue == MediaPlayer.OptionsAndroid.Resolution.Custom)
					{
#if UNITY_2017_2_OR_NEWER
						DisplayPlatformOption(optionsVarName, _optionCustomPreferredMaxResolution);
#endif
					}
				}

				{
					EditorGUILayout.BeginHorizontal();
					DisplayPlatformOption(optionsVarName, _optionCustomPreferredPeakBitRate);
					DisplayPlatformOption(optionsVarName, _optionCustomPreferredPeakBitRateUnits);
					EditorGUILayout.EndHorizontal();
				}

				DisplayPlatformOption(optionsVarName, _optionMinBufferMs);
				DisplayPlatformOption(optionsVarName, _optionMaxBufferMs);
				DisplayPlatformOption(optionsVarName, _optionBufferForPlaybackMs);
				DisplayPlatformOption(optionsVarName, _optionBufferForPlaybackAfterRebufferMs);

				EditorGUILayout.EndVertical();
			}
			GUI.enabled = true;

			/*
			SerializedProperty propFileOffsetLow = serializedObject.FindProperty(optionsVarName + ".fileOffsetLow");
			SerializedProperty propFileOffsetHigh = serializedObject.FindProperty(optionsVarName + ".fileOffsetHigh");
			if (propFileOffsetLow != null && propFileOffsetHigh != null)
			{
				propFileOffsetLow.intValue = ;

				EditorGUILayout.PropertyField(propFileOFfset);
			}*/
		}
	}
}