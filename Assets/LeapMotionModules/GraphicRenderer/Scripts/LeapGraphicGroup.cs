﻿using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Assertions;
using Leap.Unity.Space;
using Leap.Unity.Query;

namespace Leap.Unity.GraphicalRenderer {

  [AddComponentMenu("")]
  public partial class LeapGraphicGroup : LeapGraphicComponentBase<LeapGraphicRenderer> {

    #region INSPECTOR FIELDS
    [SerializeField]
    private LeapRenderingMethod _renderingMethod;

    [SerializeField]
    private List<LeapGraphicFeatureBase> _features = new List<LeapGraphicFeatureBase>();
    #endregion

    #region PRIVATE VARIABLES
    [SerializeField, HideInInspector]
    private LeapGraphicRenderer _renderer;

    [SerializeField, HideInInspector]
    private List<LeapGraphic> _graphics = new List<LeapGraphic>();

    [SerializeField, HideInInspector]
    private List<SupportInfo> _supportInfo = new List<SupportInfo>();

    [SerializeField, HideInInspector]
    private bool _addRemoveSupported;
    #endregion

    #region PUBLIC RUNTIME API

    public
#if UNITY_EDITOR
  new
#endif
  LeapGraphicRenderer renderer {
      get {
        return _renderer;
      }
    }

    public LeapRenderingMethod renderingMethod {
      get {
        return _renderingMethod;
      }
    }

    public List<LeapGraphicFeatureBase> features {
      get {
        return _features;
      }
    }

    public List<LeapGraphic> graphics {
      get {
        return _graphics;
      }
    }

    /// <summary>
    /// Maps 1-to-1 with the feature list, where each graphic represents the
    /// support that feature currently has.
    /// </summary>
    public List<SupportInfo> supportInfo {
      get {
        return _supportInfo;
      }
    }

    public bool addRemoveSupported {
      get {
        return _addRemoveSupported;
      }
    }

    public bool TryAddGraphic(LeapGraphic graphic) {
      Assert.IsNotNull(graphic);

      if (!addRemoveSupportedOrEditTime()) {
        return false;
      }

      if (_graphics.Contains(graphic)) {
        if (graphic.attachedGroup == null) {
          //detatch and re-add, it forgot it was attached!
          //This can easily happen at edit time due to prefab shenanigans 
          graphic.OnDetachedFromGroup();
        } else {
          return false;
        }
      }

      _graphics.Add(graphic);

      LeapSpaceAnchor anchor = _renderer.space == null ? null : LeapSpaceAnchor.GetAnchor(graphic.transform);

      graphic.OnAttachedToGroup(this, anchor);

      //TODO: this is gonna need to be optimized
      RebuildFeatureData();
      RebuildFeatureSupportInfo();

#if UNITY_EDITOR
      if (!Application.isPlaying) {
        _renderer.editor.ScheduleEditorUpdate();
      }

      if (_renderingMethod is ISupportsAddRemove) {
        (_renderingMethod as ISupportsAddRemove).OnAddGraphic();
      }
#else
    (_renderer as ISupportsAddRemove).OnAddGraphic();
#endif

      return true;
    }

    public bool TryRemoveGraphic(LeapGraphic graphic) {
      Assert.IsNotNull(graphic);

      if (!addRemoveSupportedOrEditTime()) {
        return false;
      }

      if (!_graphics.Contains(graphic)) {
        return false;
      }

      graphic.OnDetachedFromGroup();
      _graphics.Remove(graphic);

      //TODO: this is gonna need to be optimized
      RebuildFeatureData();
      RebuildFeatureSupportInfo();

#if UNITY_EDITOR
      if (!Application.isPlaying) {
        _renderer.editor.ScheduleEditorUpdate();
      }

      if (_renderingMethod is ISupportsAddRemove) {
        (_renderingMethod as ISupportsAddRemove).OnRemoveGraphic();
      }
#else
    (_renderer as ISupportsAddRemove).OnRemoveGraphic();
#endif

      return true;
    }

    public bool GetSupportedFeatures<T>(List<T> features) where T : LeapGraphicFeatureBase {
      features.Clear();
      for (int i = 0; i < _features.Count; i++) {
        var feature = _features[i];
        if (!(feature is T)) continue;
        if (_supportInfo[i].support == SupportType.Error) continue;

        features.Add(feature as T);
      }

      return features.Count != 0;
    }

    public void UpdateRenderer() {
      _renderingMethod.OnUpdateRenderer();

      foreach (var feature in _features) {
        feature.isDirty = false;
      }
    }

    public void RebuildFeatureData() {
      using (new ProfilerSample("Rebuild Feature Data")) {
        foreach (var feature in _features) {
          feature.ClearDataObjectReferences();
          feature.isDirty = true;
        }

        for (int i = 0; i < _graphics.Count; i++) {
          var graphic = _graphics[i];

          List<LeapFeatureData> dataList = new List<LeapFeatureData>();
          foreach (var feature in _features) {
            var dataObj = graphic.featureData.Query().OfType(feature.GetDataObjectType()).FirstOrDefault();
            if (dataObj != null) {
              graphic.featureData.Remove(dataObj);
            } else {
              dataObj = feature.CreateFeatureDataForGraphic(graphic);
            }
            feature.AddFeatureData(dataObj);
            dataList.Add(dataObj);
          }

          foreach (var dataObj in graphic.featureData) {
            DestroyImmediate(dataObj);
          }

          graphic.OnAssignFeatureData(dataList);
        }

        //Could be more efficient
        foreach (var feature in _features) {
          feature.AssignFeatureReferences();
        }
      }
    }

    public void RebuildFeatureSupportInfo() {
      using (new ProfilerSample("Rebuild Support Info")) {
        var typeToFeatures = new Dictionary<Type, List<LeapGraphicFeatureBase>>();
        foreach (var feature in _features) {
          Type featureType = feature.GetType();
          List<LeapGraphicFeatureBase> list;
          if (!typeToFeatures.TryGetValue(featureType, out list)) {
            list = new List<LeapGraphicFeatureBase>();
            typeToFeatures[featureType] = list;
          }

          list.Add(feature);
        }


        var featureToInfo = new Dictionary<LeapGraphicFeatureBase, SupportInfo>();

        foreach (var pair in typeToFeatures) {
          var featureType = pair.Key;
          var featureList = pair.Value;
          var infoList = new List<SupportInfo>().FillEach(featureList.Count, () => SupportInfo.FullSupport());

          var castList = Activator.CreateInstance(typeof(List<>).MakeGenericType(featureType)) as IList;
          foreach (var feature in featureList) {
            castList.Add(feature);
          }

          try {
            if (_renderingMethod == null) continue;

            var interfaceType = typeof(ISupportsFeature<>).MakeGenericType(featureType);
            if (!interfaceType.IsAssignableFrom(_renderingMethod.GetType())) {
              infoList.FillEach(() => SupportInfo.Error("This renderer does not support this feature."));
              continue;
            }

            var supportDelegate = interfaceType.GetMethod("GetSupportInfo");

            if (supportDelegate == null) {
              Debug.LogError("Could not find support delegate.");
              continue;
            }

            supportDelegate.Invoke(_renderingMethod, new object[] { castList, infoList });
          } finally {
            for (int i = 0; i < featureList.Count; i++) {
              featureToInfo[featureList[i]] = infoList[i];
            }
          }
        }

        _supportInfo = new List<SupportInfo>();
        foreach (var feature in _features) {
          _supportInfo.Add(feature.GetSupportInfo(this).OrWorse(featureToInfo[feature]));
        }
      }
    }
    #endregion

    #region UNITY CALLBACKS

    protected override void OnValidate() {
      base.OnValidate();

      if (_renderer == null) {
        _renderer = GetComponent<LeapGraphicRenderer>();
      }

      if (!Application.isPlaying) {
        _addRemoveSupported = true;
        if (_renderingMethod != null) {
          _addRemoveSupported &= typeof(ISupportsAddRemove).IsAssignableFrom(renderingMethod.GetType());
        }
        if (_renderer.space != null) {
          _addRemoveSupported &= typeof(ISupportsAddRemove).IsAssignableFrom(_renderer.space.GetType());
        }
      }

      for (int i = _features.Count; i-- != 0;) {
        if (_features[i] == null) {
          _features.RemoveAt(i);
        }
      }

      if (_renderingMethod != null) {
        _renderingMethod.renderer = _renderer;
        _renderingMethod.group = this;
      }
    }

#if UNITY_EDITOR
    protected override void OnDestroyedByUser() {
      editor.OnDestroyedByUser();
    }
#endif

    private void OnEnable() {
#if UNITY_EDITOR
      if (!Application.isPlaying) {
        return;
      }
#endif

      _renderingMethod.OnEnableRenderer();
    }

    private void OnDisable() {
#if UNITY_EDITOR
      if (!Application.isPlaying) {
        return;
      }
#endif

      _renderingMethod.OnDisableRenderer();
    }

    #endregion

    #region PRIVATE IMPLEMENTATION

#if UNITY_EDITOR
    private LeapGraphicGroup() {
      editor = new EditorApi(this);
    }
#endif

    private bool addRemoveSupportedOrEditTime() {
#if UNITY_EDITOR
      if (!Application.isPlaying) {
        return true;
      }
#endif

      return _addRemoveSupported;
    }
    #endregion
  }
}
