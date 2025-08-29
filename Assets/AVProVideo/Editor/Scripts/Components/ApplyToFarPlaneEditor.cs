using UnityEngine;
using UnityEditor;

//-----------------------------------------------------------------------------
// Copyright 2015-2025 RenderHeads Ltd.  All rights reserved.
//-----------------------------------------------------------------------------

namespace RenderHeads.Media.AVProVideo.Editor
{
	/// <summary>
	/// Editor for the ApplyToFarPlane component
	/// </summary>
	[CanEditMultipleObjects]
	[CustomEditor(typeof(ApplyToFarPlane))]
	public class ApplyToFarPlaneEditor : UnityEditor.Editor
	{
		private SerializedProperty _mainColor;
		private SerializedProperty _chroma;
		private SerializedProperty _alpha;
		private SerializedProperty _aspectRatio;
		private SerializedProperty _drawOffset;
		private SerializedProperty _customScaling;
		private SerializedProperty _propTextureOffset;
		private SerializedProperty _propTextureScale;
		private SerializedProperty _propMediaPlayer;
		private SerializedProperty _propMaterial;
		private SerializedProperty _propDefaultTexture;
		private SerializedProperty _mat;
		private SerializedProperty _cam;

		void OnEnable()
		{
			_mainColor = this.CheckFindProperty("_mainColor");
			_chroma = this.CheckFindProperty("_chroma");
			_alpha = this.CheckFindProperty("_alpha");
			_aspectRatio = this.CheckFindProperty("_aspectRatio");
			_drawOffset = this.CheckFindProperty("_drawOffset");
			_customScaling = this.CheckFindProperty("_customScaling");
			_propTextureOffset = this.CheckFindProperty("_offset");
			_propTextureScale = this.CheckFindProperty("_scale");
			_propMediaPlayer = this.CheckFindProperty("_media");
			_propMaterial = this.CheckFindProperty("_material");
			_propDefaultTexture = this.CheckFindProperty("_defaultTexture");
			_mat = this.CheckFindProperty("_material");
			_cam = this.CheckFindProperty("_camera");
		}

		public override void OnInspectorGUI()
		{
			serializedObject.Update();
			ApplyToFarPlane tar = (ApplyToFarPlane)target;

			// cant change of the material properties if the material does not exist
			if (_propMaterial == null)
				return;

			EditorGUI.BeginChangeCheck();

			EditorGUILayout.PropertyField(_propMediaPlayer);


			// when any of the elements are changed send a call back to the 
			// target object to update the material
			// Color
			EditorGUILayout.PropertyField(_mainColor);
			if (_mainColor.serializedObject.ApplyModifiedProperties())
				tar.UpdateMaterialProperties(0);

			// Chroma (texture)
			EditorGUILayout.PropertyField(_chroma);
			if (_chroma.serializedObject.ApplyModifiedProperties())
				tar.UpdateMaterialProperties(4);

			// Alpha slider
			EditorGUILayout.Slider(_alpha, 0, 1, "Alpha");
			if (_alpha.serializedObject.ApplyModifiedProperties())
				tar.UpdateMaterialProperties(5);

			EditorGUILayout.Space();

			// Aspect Ratio (Disabled when custom scaling is set)
			var toggle = tar.CustomScaling.x != 0 && tar.CustomScaling.y != 0;
			EditorGUI.BeginDisabledGroup(toggle);
			EditorGUILayout.PropertyField(_aspectRatio);
			if (_aspectRatio.serializedObject.ApplyModifiedProperties())
				tar.UpdateMaterialProperties(7);
			EditorGUI.EndDisabledGroup();

			// custom scaling (Vec2 of the width and height to set)
			EditorGUILayout.PropertyField(_customScaling);
			if (_customScaling.serializedObject.ApplyModifiedProperties())
				tar.UpdateMaterialProperties(9);

			// Draw offset to add to the image when rendering with shader so the image can be moved around the screen
			EditorGUILayout.PropertyField(_drawOffset);
			if (_drawOffset.serializedObject.ApplyModifiedProperties())
				tar.UpdateMaterialProperties(8);

			// default texture to show
			EditorGUILayout.PropertyField(_propDefaultTexture);
			// texture offset
			EditorGUILayout.PropertyField(_propTextureOffset);
			// texture scaling
			EditorGUILayout.PropertyField(_propTextureScale);

			EditorGUILayout.Space();

			// camera to render to 
			EditorGUILayout.PropertyField(_cam);
			// the material that is being used (automaitcally set if not set by user)
			EditorGUILayout.PropertyField(_mat);

			// when items have been updated update the displayed material
			// to ensure that the correct thing is being shown
			serializedObject.ApplyModifiedProperties();
			bool wasModified = EditorGUI.EndChangeCheck();
			if (Application.isPlaying && wasModified)
			{
				foreach (Object obj in this.targets)
				{
					((ApplyToFarPlane)obj).ForceUpdate();
				}
			}
		}
	}
}
