using UnityEngine;

using UnityEngine.Rendering.Universal;

/// <summary>
/// Component that controls the singleton camera that navigates the hex map.
/// </summary>
/// <remarks>
/// Game_2 stick 距离档位（Abs(stick.z)，越大越远）：
/// <list type="bullet">
/// <item><description>超近景：70（原最近档120之后再向上滚一格）</description></item>
/// <item><description>近景：≤ <see cref="NearViewMaxDistance"/>（500）— 部队模型 + 兵牌</description></item>
/// <item><description>中近景：500 ~ <see cref="MidViewMinDistance"/>（1000）— 仅兵牌，地形细节同近景但无地形阴影</description></item>
/// <item><description>中景：1000 ~ <see cref="MidViewMaxDistance"/>（2000）— overview 政治图</description></item>
/// <item><description>远景：平面极限后继续拉远 — 请求切换球体视图</description></item>
/// </list>
/// </remarks>
public class HexMapCamera : MonoBehaviour
{
	/// <summary>The two mutually-exclusive map presentation modes.</summary>
	public enum ViewMode
	{
		Flat,
		Globe
	}

	/// <summary>近景上限：此距离及以内展示部队模型。</summary>
	public const float NearViewMaxDistance = 500f;

	/// <summary>中景起点 / 中近景上限：超过后切入 overview 中景逻辑。</summary>
	public const float MidViewMinDistance = 1000f;

	/// <summary>中景上限；平面极限后继续拉远会请求球体远景。</summary>
	public const float MidViewMaxDistance = 2000f;

	[SerializeField]
	float stickMinZoom, stickMaxZoom;

	[Tooltip("滚轮在原最近档之后增加的超近景距离；原 stickMaxZoom 档保持不变。")]
	[SerializeField, Min(20f)]
	float extraCloseStickDistance = 70f;

	[SerializeField]
	float swivelMinZoom, swivelMaxZoom;

	[SerializeField]
	float moveSpeedMinZoom, moveSpeedMaxZoom;

	[Tooltip("以该归一化缩放为手感基准；更远时速度按摄像机距离等比放大。")]
	[SerializeField, Range(0.2f, 0.9f)]
	float moveSpeedReferenceZoom = 0.55f;

	[Tooltip("兼容旧序列化；Game_2 实际以 MidViewMinDistance(1000) 切入中景 overview。")]
	[SerializeField, Range(0.5f, 0.9f)]
	float overviewZoomThreshold = 0.78f;

	[Tooltip("When disabled, every zoom level renders real streamed chunks. " +
		"The high-altitude overview is not shown as a fallback.")]
	[SerializeField]
	bool useHighAltitudeOverview = true;

	[Tooltip("Enable the continuous ocean when zoom is at or below this value.")]
	[SerializeField, Range(0.15f, 0.85f)]
	float globalOceanEnableZoom = 0.62f;

	[Tooltip("Return to chunk water above this value. The gap prevents flicker.")]
	[SerializeField, Range(0.2f, 0.95f)]
	float globalOceanDisableZoom = 0.70f;

	[SerializeField, Min(0.01f)]
	float moveSmoothTime = 0.08f;

	[Tooltip("Hard-cap frame dt for pan so hitch spikes do not jump the camera.")]
	[SerializeField, Range(1f / 60f, 1f / 20f)]
	float maxMoveDeltaTime = 1f / 30f;

	[Tooltip("Hold LMB this long before drag pans the mid-view flat map (near/mid-near keep box-select).")]
	[SerializeField, Min(0.05f)]
	float pointerPanLongPressSeconds = 0.18f;

	[SerializeField]
	HexGrid grid;

	Transform swivel, stick;

	float zoom = 1f;
	bool extraCloseZoom;

	Vector2 smoothedMoveInput;
	Vector2 moveInputVelocity;
	float streamDampFactor = 1f;
	Camera mapCamera;
	UniversalAdditionalCameraData cameraData;
	bool defaultAllowMSAA;
	bool defaultAllowHDR;
	bool defaultOcclusionCulling;
	bool defaultRenderShadows;
	/// <summary>0 近景全质量；1 中近景降质；2 overview。</summary>
	int mapRenderTier = -1;
	bool globalOceanRequested;
	ViewMode viewMode = ViewMode.Flat;
	bool viewTransitioning;
	/// <summary>缩放/平移后合并到 LateUpdate 再推流，避免滚轮一帧多次 UpdateCameraView。</summary>
	bool cameraViewDirty;
	bool pointerPanDown;
	bool pointerPanActive;
	bool suppressMapClick;
	float pointerPanDownTime;
	Vector2 pointerPanLastScreen;
	/// <summary>锁定滚轮缩放与鼠标拖图；WASD 平移不受影响。</summary>
	bool pointerInputLocked;

	/// <summary>
	/// 中近景流式软上限。屏幕边缘用视口补块，不靠整窗放大。
	/// </summary>
	const int MidNearMinStreamRadiusX = 10;
	const int MidNearMinStreamRadiusZ = 8;
	const int MidNearMaxStreamRadiusX = 14;
	const int MidNearMaxStreamRadiusZ = 11;

	static HexMapCamera instance;
	static Vector3 globeCenter;
	static float globeRadius;
	/// <summary>由 GameManager 部队 LOD 同步：与近景模型同档才开地形阴影。</summary>
	static bool nearUnitModelsVisible;
	static bool nearUnitModelsVisibleExplicit;
	static bool landBuildSelectionPresentationActive;

	/// <summary>建造选格模式：近/中近景也切 overview 政治图，而非在 relief 上叠色。</summary>
	public static bool LandBuildSelectionPresentationActive =>
		landBuildSelectionPresentationActive;

	public static void SetLandBuildSelectionPresentation(bool active)
	{
		if (landBuildSelectionPresentationActive == active)
		{
			return;
		}

		landBuildSelectionPresentationActive = active;
		if (instance)
		{
			instance.cameraViewDirty = true;
		}
	}

	/// <summary>
	/// Raised when scroll input asks the presentation controller to switch view.
	/// <see cref="CurrentViewMode"/> has already changed when this event runs.
	/// Programmatic calls to <see cref="SetViewMode"/> do not raise this event.
	/// </summary>
	public static event System.Action<ViewMode> ViewModeRequested;

	/// <summary>
	/// Raised whenever the active presentation mode changes, including
	/// programmatic changes made through <see cref="SetViewMode"/>.
	/// </summary>
	public static event System.Action<ViewMode> ViewModeChanged;

	/// <summary>
	/// Whether the singleton camera controls are locked.
	/// </summary>
	public static bool Locked
	{
		set => SetLocked(value);
	}

	public static bool HasInstance => instance;
	// A game presentation can own the camera without disabling this component:
	// FairyGUI continues to control the existing keyboard and pointer locks.
	public bool ExternalNavigation { get; set; }
	public static bool NavigationInputEnabled => !instance || instance.isActiveAndEnabled;
	public static bool NavigationPointerLocked => instance && instance.pointerInputLocked;

	/// <summary>
	/// 与近景部队模型展示同档；未收到 GameManager 同步时回退为 stick ≤ <see cref="NearViewMaxDistance"/>。
	/// </summary>
	public static bool NearUnitModelsVisible =>
		nearUnitModelsVisibleExplicit
			? nearUnitModelsVisible
			: !instance || CurrentStickDistance <= NearViewMaxDistance;

	/// <summary>Game_2：部队 VisualLod==0 时设为 true，与模型/兵牌滞回一致。</summary>
	public static void SetNearUnitModelsVisible(bool visible)
	{
		nearUnitModelsVisibleExplicit = true;
		if (nearUnitModelsVisible == visible)
		{
			return;
		}

		nearUnitModelsVisible = visible;
		if (instance && instance.grid)
		{
			instance.ApplyMapRenderQuality(instance.grid.IsOverviewMode, force: true);
		}
	}

	/// <summary>
	/// Presentation-only globe surface shared with projection-agnostic overlays
	/// such as country names. The simulation remains owned by the flat grid.
	/// </summary>
	public static bool TryGetGlobeSurface(out Vector3 center, out float radius)
	{
		center = globeCenter;
		radius = globeRadius;
		return radius > 0f;
	}

	public static void SetGlobeSurface(Vector3 center, float radius)
	{
		globeCenter = center;
		globeRadius = Mathf.Max(0f, radius);
	}

	/// <summary>The presentation mode currently requested by the map camera.</summary>
	public static ViewMode CurrentViewMode =>
		instance ? instance.viewMode : ViewMode.Flat;

	/// <summary>True while an external presentation controller is animating a view change.</summary>
	public static bool IsViewTransitioning =>
		instance && instance.viewTransitioning;

	/// <summary>
	/// True while the flat-map presentation should remain visible. Unlike
	/// <see cref="IsFlatInteractive"/>, temporary UI input locks do not affect it.
	/// </summary>
	public static bool IsFlatPresentationActive =>
		!instance || (!instance.viewTransitioning && instance.viewMode == ViewMode.Flat);

	/// <summary>
	/// True while the flat map can accept input. Without a camera instance this
	/// defaults to true so editor and isolated test scenes retain legacy behavior.
	/// Globe controllers can use this to gate flat-map picking and overlays.
	/// </summary>
	public static bool IsFlatInteractive =>
		!instance || (instance.isActiveAndEnabled &&
		!instance.viewTransitioning && instance.viewMode == ViewMode.Flat);

	/// <summary>
	/// True while WASD/arrow pan is held, long-press drag pan is active, or the
	/// camera is still coasting from move smoothing.
	/// </summary>
	public static bool IsPanInputActive
	{
		get
		{
			if (!instance || !IsFlatInteractive)
			{
				return false;
			}

			if (instance.pointerPanActive)
			{
				return true;
			}

			float horizontal = Input.GetAxisRaw("Horizontal");
			float vertical = Input.GetAxisRaw("Vertical");
			if (horizontal * horizontal + vertical * vertical > 0.01f)
			{
				return true;
			}

			return instance.smoothedMoveInput.sqrMagnitude > 0.0001f;
		}
	}

	/// <summary>
	/// True after a long-press map pan started for the current LMB gesture.
	/// Area / unit click handlers should skip selection while this is set.
	/// </summary>
	public static bool ShouldSuppressMapClick =>
		instance && instance.suppressMapClick;

	/// <summary>True while long-press LMB is actively dragging the flat map.</summary>
	public static bool IsPointerMapPanning =>
		instance && instance.pointerPanActive && IsFlatInteractive;

	/// <summary>
	/// 近景与中近景生成城市/植被等地图物件；中景 overview 不生成。
	/// 无地图摄像机时默认开启（编辑器场景）。
	/// </summary>
	public static bool MapDetailFeaturesEnabled =>
		!instance || CurrentStickDistance <= MidViewMinDistance;

	/// <summary>
	/// Original normalized zoom, where 0 is world overview and 1 is the original
	/// closest step. <see cref="IsExtraCloseZoom"/> distinguishes the added step.
	/// </summary>
	public static float CurrentZoom => instance ? instance.zoom : 0f;

	/// <summary>True only for the extra wheel step beyond the original closest view.</summary>
	public static bool IsExtraCloseZoom => instance && instance.extraCloseZoom;

	/// <summary>
	/// Enter or leave the extra-close wheel step. Enabling first restores the
	/// original closest zoom so all earlier wheel positions remain unchanged.
	/// </summary>
	public static void SetExtraCloseZoom(bool enabled)
	{
		if (!instance)
		{
			return;
		}
		instance.SetExtraCloseZoomInternal(enabled);
	}

	/// <summary>当前 stick 距离（绝对值，越大越远）。</summary>
	public static float CurrentStickDistance
	{
		get
		{
			if (!instance)
			{
				return 0f;
			}
			if (instance.stick)
			{
				return Mathf.Abs(instance.stick.localPosition.z);
			}
			return Mathf.Abs(Mathf.Lerp(
				instance.stickMinZoom, instance.stickMaxZoom, instance.zoom));
		}
	}

	/// <summary>
	/// Same zoom gate HexMapCamera uses to request the cheap world overview
	/// instead of streamed detailed terrain chunks.
	/// </summary>
	public static float OverviewZoomThreshold
	{
		get
		{
			if (!instance)
			{
				return DistanceToZoom(MidViewMinDistance);
			}
			return instance.ZoomAtDistance(MidViewMinDistance);
		}
	}

	/// <summary>
	/// True while stick distance is in the mid/far overview band
	/// (&gt; <see cref="MidViewMinDistance"/>). Detailed hex textures are not
	/// streamed in this range.
	/// </summary>
	public static bool IsOverviewZoom
	{
		get
		{
			if (!instance || instance.viewMode != ViewMode.Flat ||
				!instance.useHighAltitudeOverview)
			{
				return false;
			}
			return CurrentStickDistance > MidViewMinDistance;
		}
	}

	static float DistanceToZoom(float stickDistance)
	{
		// Fallback when no instance: assume Game_2 stick range.
		return Mathf.InverseLerp(MidViewMaxDistance, 120f, stickDistance);
	}

	float ZoomAtDistance(float stickDistance)
	{
		float min = Mathf.Abs(stickMinZoom);
		float max = Mathf.Abs(stickMaxZoom);
		if (min <= max + 0.01f)
		{
			return 0f;
		}
		return Mathf.Clamp01(Mathf.InverseLerp(min, max, stickDistance));
	}

	/// <summary>Enable or disable map input without requiring a camera reference.</summary>
	public static void SetLocked(bool locked)
	{
		if (instance && instance.enabled == locked)
		{
			instance.enabled = !locked;
		}

		if (instance)
		{
			instance.pointerInputLocked = locked;
		}
	}

	/// <summary>
	/// 仅锁定鼠标缩放/拖图；键盘 WASD 平移仍可用（如悬停地图兵牌时）。
	/// </summary>
	public static void SetPointerLocked(bool locked)
	{
		if (instance)
		{
			instance.pointerInputLocked = locked;
		}
	}

	/// <summary>
	/// Set the presentation mode without treating the change as user input.
	/// Use this when a globe controller needs to restore or synchronize state.
	/// </summary>
	public static void SetViewMode(ViewMode mode)
	{
		if (instance)
		{
			instance.SetViewModeInternal(mode);
		}
	}

	/// <summary>
	/// Gate flat-map interaction while an external controller animates between
	/// presentations. The controller should clear this flag when the transition ends.
	/// </summary>
	public static void SetViewTransitioning(bool transitioning)
	{
		if (!instance || instance.viewTransitioning == transitioning)
		{
			return;
		}

		instance.viewTransitioning = transitioning;
		if (transitioning)
		{
			instance.ResetPanState();
		}
	}

	/// <summary>
	/// Validate the position of the singleton camera.
	/// </summary>
	public static void ValidatePosition()
	{
		if (instance && instance.grid)
		{
			instance.ValidatePositionAndStreaming();
		}
	}

	/// <summary>
	/// Clamp/wrap the current pivot and publish it to map streaming even while a
	/// presentation transition is holding normal camera input. This is used when
	/// the globe hands a new geographic focus back to the flat map.
	/// </summary>
	void ValidatePositionAndStreaming()
	{
		if (ExternalNavigation) return;
		Vector3 position = transform.localPosition;
		transform.localPosition = grid.Wrapping ?
			WrapPosition(position) : ClampPosition(position);
		if (viewMode != ViewMode.Flat)
		{
			return;
		}

		grid.UpdateCameraView(
			transform.position,
			ShouldRequestOverview(),
			GetRequestedStreamingRadii(),
			EvaluateGlobalOceanRequest(ShouldRequestOverview()),
			GetStreamingFocusWorldPosition());
	}

	bool ShouldRequestOverview()
	{
		return useHighAltitudeOverview &&
			(CurrentStickDistance > MidViewMinDistance ||
			 landBuildSelectionPresentationActive);
	}

	/// <summary>
	/// Put the startup view over the central Atlantic/Africa portion of a world
	/// map instead of inheriting the tiny editor map's south-west corner.
	/// </summary>
	public static void FocusWorldMap()
	{
		if (instance && instance.ExternalNavigation) return;
		if (!instance || !instance.grid)
		{
			return;
		}

		float width = instance.grid.CellCountX * HexMetrics.innerDiameter;
		float height = (instance.grid.CellCountZ - 1) *
			(1.5f * HexMetrics.outerRadius);
		Vector3 position = instance.transform.localPosition;
		position.x = width * 0.53f;
		position.z = height * 0.60f;
		instance.transform.localPosition = instance.grid.Wrapping ?
			instance.WrapPosition(position) : instance.ClampPosition(position);
		instance.SetZoom(0f);
	}

	/// <summary>
	/// Frame a newly-created map after leaving the high world overview.
	/// </summary>
	public static void FocusCreatedMap()
	{
		if (!instance || !instance.grid)
		{
			return;
		}

		float width = instance.grid.CellCountX * HexMetrics.innerDiameter;
		float height = (instance.grid.CellCountZ - 1) *
			(1.5f * HexMetrics.outerRadius);
		Vector3 position = instance.transform.localPosition;
		position.x = width * 0.5f;
		position.z = height * 0.5f;
		instance.transform.localPosition = instance.grid.Wrapping ?
			instance.WrapPosition(position) : instance.ClampPosition(position);

		float desiredDistance = -Mathf.Clamp(
			Mathf.Max(width * 0.65f, height * 0.9f), 90f,
			Mathf.Abs(instance.stickMinZoom));
		float targetZoom = Mathf.InverseLerp(
			instance.stickMinZoom, instance.stickMaxZoom, desiredDistance);
		instance.SetZoom(targetZoom);
	}

	void Awake()
	{
		swivel = transform.GetChild(0);
		stick = swivel.GetChild(0);
		mapCamera = GetComponentInChildren<Camera>(true);
		if (mapCamera)
		{
			cameraData = mapCamera.GetComponent<UniversalAdditionalCameraData>();
			defaultAllowMSAA = mapCamera.allowMSAA;
			defaultAllowHDR = mapCamera.allowHDR;
			defaultOcclusionCulling = mapCamera.useOcclusionCulling;
			defaultRenderShadows = !cameraData || cameraData.renderShadows;
		}
	}

	void OnEnable()
	{
		instance = this;
		transform.localRotation = Quaternion.identity;
		ValidatePosition();
	}

	void OnDisable()
	{
		HexOverviewAtmosphere.SetActive(false);
		// Do not clear HexGlobeAtmosphere here: GameManager locks this
		// component for FairyGUI focus, and the globe look must persist.
		ResetPanState();
	}

	void OnDestroy()
	{
		// Destruction is not a request to enter the detailed rendering tier.
		// Explicit restoration also works when only this controller is removed
		// and the child camera survives; it must not rebuild streamed chunks.
		HexNearTerrainLighting.Apply(mapCamera, false);
		HexOverviewAtmosphere.SetActive(false);
		if (mapCamera)
		{
			mapCamera.allowMSAA = defaultAllowMSAA;
			mapCamera.allowHDR = defaultAllowHDR;
			mapCamera.useOcclusionCulling = defaultOcclusionCulling;
		}
		if (cameraData) cameraData.renderShadows = defaultRenderShadows;
		if (instance == this)
		{
			instance = null;
		}
	}

	void Update()
	{
		if (ExternalNavigation) return;
		if (viewTransitioning)
		{
			return;
		}

		if (pointerInputLocked)
		{
			ResetPanState();
			return;
		}

		float zoomDelta = Input.GetAxis("Mouse ScrollWheel");
		if (viewMode == ViewMode.Globe)
		{
			// Wheel up returns to the preserved flat-map pose. Wheel down is
			// intentionally ignored because globe zoom belongs to its controller.
			if (zoomDelta > 0f)
			{
				RequestViewMode(ViewMode.Flat);
			}
			return;
		}

		// Sample mid-view drag in Update so LMB down is not missed relative to UI.
		UpdatePointerPan();

		if (zoomDelta != 0f)
		{
			AdjustZoom(zoomDelta);
		}
	}

	/// <summary>
	/// Pan in LateUpdate so overlays / streaming see the final pose this frame,
	/// and so hitchy Update work earlier in the frame does not sample a mid-move camera.
	/// </summary>
	void LateUpdate()
	{
		if (ExternalNavigation) return;
		if (viewTransitioning || viewMode != ViewMode.Flat)
		{
			ResetPanState();
			if (cameraViewDirty && viewMode == ViewMode.Flat && !viewTransitioning)
			{
				FlushCameraViewIfDirty();
			}
			return;
		}

		Vector2 targetMoveInput = Vector2.ClampMagnitude(
			new Vector2(
				Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical")),
			1f);
		float dt = Mathf.Min(Time.deltaTime, maxMoveDeltaTime);
		float effectiveSmoothTime = moveSmoothTime > 0.01f ?
			moveSmoothTime : 0.08f;
		smoothedMoveInput = Vector2.SmoothDamp(
			smoothedMoveInput, targetMoveInput, ref moveInputVelocity,
			effectiveSmoothTime, Mathf.Infinity, dt);
		if (smoothedMoveInput.sqrMagnitude > 0.0001f)
		{
			AdjustPosition(smoothedMoveInput.x, smoothedMoveInput.y, dt);
		}
		else if (targetMoveInput == Vector2.zero)
		{
			smoothedMoveInput = Vector2.zero;
			moveInputVelocity = Vector2.zero;
			streamDampFactor = 1f;
		}

		FlushCameraViewIfDirty();
	}

	void UpdatePointerPan()
	{
		// 近景/中近景留给框选部队；仅中景（stick > 1000）支持长按拖图。
		bool allowPointerPan = viewMode == ViewMode.Flat &&
			CurrentStickDistance > MidViewMinDistance;
		if (!allowPointerPan)
		{
			if (pointerPanDown || pointerPanActive)
			{
				pointerPanDown = false;
				pointerPanActive = false;
			}
			return;
		}

		Vector2 pointer = Input.mousePosition;
		if (Input.GetMouseButtonDown(0))
		{
			pointerPanDown = true;
			pointerPanActive = false;
			suppressMapClick = false;
			pointerPanDownTime = Time.unscaledTime;
			pointerPanLastScreen = pointer;
		}

		if (pointerPanDown && Input.GetMouseButton(0))
		{
			// 中景无框选：按住超过阈值即进入拖图，不因微动取消。
			if (!pointerPanActive &&
				Time.unscaledTime - pointerPanDownTime >=
				pointerPanLongPressSeconds)
			{
				pointerPanActive = true;
				suppressMapClick = true;
				pointerPanLastScreen = pointer;
			}

			if (pointerPanActive)
			{
				ApplyPointerPanScreenDelta(pointer - pointerPanLastScreen);
			}

			pointerPanLastScreen = pointer;
		}

		if (!Input.GetMouseButton(0))
		{
			pointerPanDown = false;
			pointerPanActive = false;
			// Keep suppressMapClick until the next LMB down so Update-time
			// click handlers on this release frame still see it.
		}
	}

	void ApplyPointerPanScreenDelta(Vector2 screenDelta)
	{
		if (!mapCamera || screenDelta.sqrMagnitude < 1e-6f)
		{
			return;
		}

		// Prefer grab-map via ground rays; fall back to pixel→world if rays miss.
		if (TryGetGroundPointUnderScreen(pointerPanLastScreen, out Vector3 prev) &&
			TryGetGroundPointUnderScreen(pointerPanLastScreen + screenDelta, out Vector3 curr))
		{
			ApplyFlatPanWorldShift(prev - curr);
			return;
		}

		Vector3 right = mapCamera.transform.right;
		right.y = 0f;
		if (right.sqrMagnitude < 1e-6f)
		{
			return;
		}
		right.Normalize();
		Vector3 forward = Vector3.Cross(right, Vector3.up);

		if (!TryGetGroundPointUnderScreen(
			new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f),
			out Vector3 groundCenter))
		{
			return;
		}

		float camDist = Vector3.Distance(
			mapCamera.transform.position, groundCenter);
		float pixelToWorld = 2f * camDist *
			Mathf.Tan(mapCamera.fieldOfView * 0.5f * Mathf.Deg2Rad) /
			Mathf.Max(1f, Screen.height);
		ApplyFlatPanWorldShift(
			(-right * screenDelta.x - forward * screenDelta.y) * pixelToWorld);
	}

	void ApplyFlatPanWorldShift(Vector3 worldShift)
	{
		worldShift.y = 0f;
		if (worldShift.sqrMagnitude < 1e-8f)
		{
			return;
		}

		if (transform.parent)
		{
			worldShift = transform.parent.InverseTransformDirection(worldShift);
			worldShift.y = 0f;
		}

		Vector3 position = transform.localPosition + worldShift;
		transform.localPosition = grid && grid.Wrapping
			? WrapPosition(position)
			: ClampPosition(position);
		cameraViewDirty = true;
	}

	void AdjustZoom(float delta)
	{
		// A separate scroll step after reaching the farthest legal flat zoom
		// requests the globe. Keeping this test out of SetZoom ensures startup
		// calls such as FocusWorldMap -> SetZoom(0) can never trigger it.
		if (delta < 0f && IsAtFarthestFlatZoom())
		{
			RequestViewMode(ViewMode.Globe);
			return;
		}

		// HOI4 式：缩放时保持鼠标下地面点，相机枢轴向该点靠拢。
		Vector3 mouse = Input.mousePosition;
		Vector3 focusBefore = default;
		bool hasFocus = viewMode == ViewMode.Flat
			&& TryGetGroundPointUnderScreen(mouse, out focusBefore);

		float previousZoom = zoom;
		bool wasExtraClose = extraCloseZoom;
		if (delta > 0f && zoom >= 0.9999f)
		{
			SetExtraCloseZoomInternal(true);
		}
		else if (delta < 0f && extraCloseZoom)
		{
			SetExtraCloseZoomInternal(false);
		}
		else
		{
			SetZoom(zoom + delta);
		}

		if (!hasFocus || (Mathf.Abs(zoom - previousZoom) < 1e-6f &&
			extraCloseZoom == wasExtraClose))
		{
			return;
		}

		if (TryGetGroundPointUnderScreen(mouse, out Vector3 focusAfter))
		{
			Vector3 shift = focusBefore - focusAfter;
			shift.y = 0f;
			Vector3 position = transform.localPosition + shift;
			transform.localPosition = grid && grid.Wrapping
				? WrapPosition(position)
				: ClampPosition(position);
			cameraViewDirty = true;
		}
	}

	void SetZoom(float value)
	{
		extraCloseZoom = false;
		zoom = Mathf.Clamp01(value);
		ApplyStickAndSwivel();
		ClampZoomToMapBounds();
		ApplyStickAndSwivel();
		ClampCameraToMapBounds();
		if (viewMode != ViewMode.Flat)
		{
			return;
		}

		cameraViewDirty = true;
	}

	void SetExtraCloseZoomInternal(bool enabled)
	{
		if (enabled)
		{
			zoom = 1f;
		}
		if (extraCloseZoom == enabled)
		{
			return;
		}

		extraCloseZoom = enabled;
		ApplyStickAndSwivel();
		ClampCameraToMapBounds();
		if (viewMode == ViewMode.Flat)
		{
			cameraViewDirty = true;
		}
	}

	bool hasStreamingCameraState;
	Matrix4x4 lastStreamingCameraProjection, lastStreamingCameraRelativePose;
	Rect lastStreamingCameraPixelRect;
	uint lastStreamingCameraSurfaceRevision, lastStreamingCameraShapeRevision;
	int lastStreamingCameraStyleRevision, lastStreamingCameraWaterLevel;
	HexTerrainStyle lastStreamingCameraStyle;
	HexNearTerrainProfile lastStreamingCameraProfile;

	bool DetectStreamingCameraChange()
	{
		if (!mapCamera || !grid) return false;
		Matrix4x4 projection = mapCamera.projectionMatrix;
		Matrix4x4 relativePose = grid.transform.worldToLocalMatrix * mapCamera.cameraToWorldMatrix;
		Rect pixelRect = mapCamera.pixelRect;
		HexTerrainStyle style = grid.SurfaceStyle;
		HexNearTerrainProfile profile = style ? style.nearTerrainProfile : null;
		int styleRevision = style ? style.RuntimeRevision : 0;
		uint shapeRevision = profile ? profile.ShapeRevision : 0;
		if (hasStreamingCameraState && projection.Equals(lastStreamingCameraProjection) &&
			relativePose.Equals(lastStreamingCameraRelativePose) && pixelRect.Equals(lastStreamingCameraPixelRect) &&
			grid.SurfaceRevision == lastStreamingCameraSurfaceRevision && style == lastStreamingCameraStyle &&
			profile == lastStreamingCameraProfile && styleRevision == lastStreamingCameraStyleRevision &&
			shapeRevision == lastStreamingCameraShapeRevision && HexMetrics.visualWaterLevel == lastStreamingCameraWaterLevel)
			return false;
		hasStreamingCameraState = true;
		lastStreamingCameraProjection = projection; lastStreamingCameraRelativePose = relativePose;
		lastStreamingCameraPixelRect = pixelRect; lastStreamingCameraSurfaceRevision = grid.SurfaceRevision;
		lastStreamingCameraStyle = style; lastStreamingCameraProfile = profile;
		lastStreamingCameraStyleRevision = styleRevision; lastStreamingCameraShapeRevision = shapeRevision;
		lastStreamingCameraWaterLevel = HexMetrics.visualWaterLevel;
		return true;
	}

	void FlushCameraViewIfDirty()
	{
		if (!grid || viewMode != ViewMode.Flat) return;
		// Resizing, FOV/projection edits and external camera/grid movement also
		// change coverage while pan/zoom input is idle. Exact value snapshots are
		// allocation-free and leave a stationary unchanged camera dormant.
		if (DetectStreamingCameraChange()) cameraViewDirty = true;
		if (!cameraViewDirty) return;

		cameraViewDirty = false;
		bool requestOverview = ShouldRequestOverview();
		grid.UpdateCameraView(
			transform.position,
			requestOverview,
			GetRequestedStreamingRadii(),
			EvaluateGlobalOceanRequest(requestOverview),
			GetStreamingFocusWorldPosition());
		ApplyMapRenderQuality(grid.IsOverviewMode);
	}

	bool TryGetGroundPointUnderScreen(Vector3 screenPosition, out Vector3 groundPoint)
	{
		groundPoint = default;
		if (!mapCamera)
		{
			return false;
		}

		Ray ray = mapCamera.ScreenPointToRay(screenPosition);
		float groundY = grid ? grid.transform.position.y : 0f;
		if (Mathf.Abs(ray.direction.y) <= 1e-4f)
		{
			return false;
		}

		float t = (groundY - ray.origin.y) / ray.direction.y;
		if (t <= 0f)
		{
			return false;
		}

		groundPoint = ray.GetPoint(t);
		return true;
	}

	bool IsAtFarthestFlatZoom()
	{
		if (zoom <= 0.0001f)
		{
			return true;
		}
		if (!grid || !stick)
		{
			return false;
		}

		float angle = Mathf.Lerp(swivelMinZoom, swivelMaxZoom, zoom);
		float maxDistance = GetMaxFitStickDistance(angle);
		float tolerance = Mathf.Max(0.1f, maxDistance * 0.0001f);
		return Mathf.Abs(stick.localPosition.z) >= maxDistance - tolerance;
	}

	void RequestViewMode(ViewMode mode)
	{
		if (viewMode == mode)
		{
			return;
		}

		System.Action<ViewMode> handler = ViewModeRequested;
		if (handler == null)
		{
			// Shared terrain-authoring/reference scenes do not install a globe
			// controller. Keep their legacy flat-map zoom behavior unchanged.
			return;
		}

		SetViewModeInternal(mode);
		handler.Invoke(mode);
	}

	void SetViewModeInternal(ViewMode mode)
	{
		if (viewMode == mode)
		{
			return;
		}

		viewMode = mode;
		ResetPanState();
		if (mode == ViewMode.Flat)
		{
			// Re-prime streaming at the unchanged pivot before the flat map is
			// shown again. SetZoom does not generate another mode request.
			SetZoom(zoom);
		}
		else
		{
			HexNearTerrainLighting.Apply(mapCamera, false);
			HexOverviewAtmosphere.SetActive(false);
			// Globe atmosphere is owned by WorldMapGlobeTransitionController
			// for the whole time the sphere is on screen.
		}
		ViewModeChanged?.Invoke(mode);
	}

	void ResetPanState()
	{
		smoothedMoveInput = Vector2.zero;
		moveInputVelocity = Vector2.zero;
		streamDampFactor = 1f;
		pointerPanDown = false;
		pointerPanActive = false;
		suppressMapClick = false;
	}

	void ApplyStickAndSwivel()
	{
		float originalClosest = Mathf.Abs(stickMaxZoom);
		float extraClosest = Mathf.Clamp(extraCloseStickDistance,
			20f, Mathf.Max(20f, originalClosest - 1f));
		float distance = extraCloseZoom ? -extraClosest :
			Mathf.Lerp(stickMinZoom, stickMaxZoom, zoom);
		stick.localPosition = new Vector3(0f, 0f, distance);
		float angle = Mathf.Lerp(swivelMinZoom, swivelMaxZoom, zoom);
		swivel.localRotation = Quaternion.Euler(angle, 0f, 0f);
	}

	/// <summary>
	/// 滚轮拉远时限制 stick 距离，使地面视野不超过地图范围。
	/// </summary>
	void ClampZoomToMapBounds()
	{
		if (!grid || !stick)
		{
			return;
		}

		for (int i = 0; i < 8; i++)
		{
			float angle = Mathf.Lerp(swivelMinZoom, swivelMaxZoom, zoom);
			float maxDistance = GetMaxFitStickDistance(angle);
			float distance = Mathf.Abs(stick.localPosition.z);
			if (distance <= maxDistance + 0.01f)
			{
				return;
			}

			float limited = -maxDistance;
			zoom = Mathf.Clamp01(Mathf.InverseLerp(
				stickMinZoom, stickMaxZoom, limited));
			ApplyStickAndSwivel();
		}
	}

	float GetMaxFitStickDistance(float swivelAngleDegrees)
	{
		GetMapGroundSize(out float mapWidth, out float mapHeight);
		float tanHalfFov = GetHalfFovTan();
		float sinAngle = Mathf.Max(
			0.35f, Mathf.Sin(Mathf.Abs(swivelAngleDegrees) * Mathf.Deg2Rad));
		// halfExtent = stickDist * tan(fov/2) / sin(swivel)
		float maxByZ = (mapHeight * 0.5f) * sinAngle / tanHalfFov;
		float maxDistance = maxByZ;
		if (!grid.Wrapping)
		{
			float aspect = mapCamera ? Mathf.Max(1f, mapCamera.aspect) : 1.78f;
			float maxByX =
				(mapWidth * 0.5f / aspect) * sinAngle / tanHalfFov;
			maxDistance = Mathf.Min(maxDistance, maxByX);
		}

		return Mathf.Min(Mathf.Abs(stickMinZoom), Mathf.Max(1f, maxDistance));
	}

	float GetHalfFovTan()
	{
		float fov = mapCamera ? mapCamera.fieldOfView : 60f;
		return Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad);
	}

	void GetMapGroundSize(out float width, out float height)
	{
		width = grid.CellCountX * HexMetrics.innerDiameter;
		height = (grid.CellCountZ - 1) * (1.5f * HexMetrics.outerRadius);
	}

	void GetGroundViewHalfExtents(out float halfX, out float halfZ)
	{
		float cameraDistance = Mathf.Abs(stick.localPosition.z);
		float angle = Mathf.Lerp(swivelMinZoom, swivelMaxZoom, zoom);
		float halfVertical = cameraDistance * GetHalfFovTan();
		halfVertical /= Mathf.Max(
			0.35f, Mathf.Sin(Mathf.Abs(angle) * Mathf.Deg2Rad));
		halfZ = halfVertical;
		halfX = halfVertical *
			(mapCamera ? Mathf.Max(1f, mapCamera.aspect) : 1.78f);
	}

	void ClampCameraToMapBounds()
	{
		if (!grid)
		{
			return;
		}

		transform.localPosition = grid.Wrapping ?
			WrapPosition(transform.localPosition) :
			ClampPosition(transform.localPosition);
	}

	bool EvaluateGlobalOceanRequest(bool requestOverview)
	{
		if (requestOverview)
		{
			globalOceanRequested = false;
			return false;
		}

		// 中近景整段使用全球海洋，跳过纯海 chunk；进入近景后再切回分块水域。
		float distance = Mathf.Abs(stick.localPosition.z);
		float enableDistance = NearViewMaxDistance + 40f;
		float disableDistance = NearViewMaxDistance - 40f;
		globalOceanRequested = globalOceanRequested ?
			distance > disableDistance : distance > enableDistance;
		return globalOceanRequested;
	}

	void ApplyMapRenderQuality(bool overview, bool force = false)
	{
		if (!mapCamera)
		{
			return;
		}

		int tier = overview ? 2 :
			(CurrentStickDistance > NearViewMaxDistance ? 1 : 0);
		bool terrainDetail = !overview && viewMode == ViewMode.Flat;
		bool useTerrainLighting = terrainDetail && NearUnitModelsVisible && grid &&
			grid.SurfaceStyle && grid.SurfaceStyle.UsesNearTerrain;
		// A streamed grid can acquire its style after the camera's first frame.
		// Check the binding even when zoom stays in the same quality tier.
		HexNearTerrainLighting.Apply(mapCamera,
			useTerrainLighting, CurrentStickDistance);

		// A late-bound style also needs its camera flags updated in the same tier.
		// 地形细节仍覆盖近/中近景；阴影与近景模型同档（见 NearUnitModelsVisible）。
		mapCamera.allowHDR = terrainDetail && defaultAllowHDR;
		if (cameraData)
		{
			cameraData.renderShadows = useTerrainLighting ||
				(terrainDetail && NearUnitModelsVisible && defaultRenderShadows);
		}

		HexOverviewAtmosphere.SetActive(overview);
		if (!force && mapRenderTier == tier)
		{
			return;
		}

		mapRenderTier = tier;

		if (grid)
		{
			// Only when tier changes — pan must not re-push globals every frame.
			grid.RefreshSurfaceOverlayShaderGlobals();
		}

		// 地图相机关闭 MSAA；近/中近景共享地形细节，阴影仅近景模型档。
		mapCamera.allowMSAA = false;
		mapCamera.useOcclusionCulling =
			!overview && defaultOcclusionCulling;

		// Retain terrain and existing detail batches across zoom bands. Chunks
		// first built at medium range create only their missing details on demand.
		if (grid) grid.SetDetailFeaturesVisible(terrainDetail);
	}

	// Fallback/prefetch request for calls without a valid camera footprint.
	// Actual visible coverage is selected continuously from the camera frustum
	// by HexGrid, independent of these historical radius caps.
	Vector2Int GetRequestedStreamingRadii()
	{
		float chunkWidth =
			HexMetrics.innerDiameter * HexMetrics.chunkSizeX;
		float chunkHeight = HexMetrics.outerRadius * 1.5f *
			HexMetrics.chunkSizeZ;
		float cameraDistance = Mathf.Abs(stick.localPosition.z);
		float halfVerticalView = mapCamera ?
			cameraDistance * Mathf.Tan(
				mapCamera.fieldOfView * 0.5f * Mathf.Deg2Rad) :
			cameraDistance * 0.58f;
		float angle = Mathf.Lerp(swivelMinZoom, swivelMaxZoom, zoom);
		// An oblique camera projects farther along map Z than a top-down camera.
		halfVerticalView /= Mathf.Max(
			0.35f, Mathf.Sin(Mathf.Abs(angle) * Mathf.Deg2Rad));
		float halfHorizontalView = halfVerticalView *
			(mapCamera ? Mathf.Max(1f, mapCamera.aspect) : 1.78f);

		int padding = cameraDistance > NearViewMaxDistance ? 1 : 2;
		int radiusX = Mathf.Clamp(
			Mathf.CeilToInt(halfHorizontalView / chunkWidth) + padding,
			1, 36);
		int radiusZ = Mathf.Clamp(
			Mathf.CeilToInt(halfVerticalView / chunkHeight) + padding,
			1, 36);

		// 中近景：小幅随距离抬高；上侧/左右靠 look-at 中心，避免过大窗口砸性能。
		if (cameraDistance > NearViewMaxDistance)
		{
			float t = Mathf.InverseLerp(
				NearViewMaxDistance, MidViewMinDistance, cameraDistance);
			int maxX = Mathf.RoundToInt(Mathf.Lerp(
				MidNearMinStreamRadiusX, MidNearMaxStreamRadiusX, t));
			int maxZ = Mathf.RoundToInt(Mathf.Lerp(
				MidNearMinStreamRadiusZ, MidNearMaxStreamRadiusZ, t));
			radiusX = Mathf.Min(radiusX, maxX);
			radiusZ = Mathf.Min(radiusZ, maxZ);
		}
		else
		{
			radiusX = Mathf.Min(radiusX, 12);
			radiusZ = Mathf.Min(radiusZ, 9);
		}

		return new Vector2Int(radiusX, radiusZ);
	}

	/// <summary>
	/// Ground look-at under screen center, nudged slightly toward the far side
	/// so top/left/right cover without enlarging the whole stream window.
	/// </summary>
	Vector3 GetStreamingFocusWorldPosition()
	{
		if (!mapCamera)
		{
			return transform.position;
		}

		Ray ray = mapCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
		float groundY = grid ? grid.transform.position.y : 0f;
		if (Mathf.Abs(ray.direction.y) <= 1e-4f)
		{
			return transform.position;
		}

		float t = (groundY - ray.origin.y) / ray.direction.y;
		if (t <= 0f)
		{
			return transform.position;
		}

		Vector3 focus = ray.GetPoint(t);
		// Bias toward look direction (screen top / far horizon).
		Vector3 flatForward = mapCamera.transform.forward;
		flatForward.y = 0f;
		if (flatForward.sqrMagnitude > 1e-4f)
		{
			flatForward.Normalize();
			float chunkSpan = HexMetrics.outerRadius * 1.5f * HexMetrics.chunkSizeZ;
			focus += flatForward * (chunkSpan * 1.1f);
		}
		return focus;
	}

	void AdjustPosition(float xDelta, float zDelta, float deltaTime)
	{
		if (viewTransitioning || viewMode != ViewMode.Flat)
		{
			return;
		}

		Vector3 direction = Vector3.ClampMagnitude(
			new Vector3(xDelta, 0f, zDelta), 1f);
		float speed = GetDistanceScaledMoveSpeed();
		// Ease stream dampening — hard 0.75 toggles read as stutter while panning.
		float dampTarget = grid && grid.ShouldDampenCameraForStreaming ?
			0.78f : 1f;
		streamDampFactor = Mathf.MoveTowards(
			streamDampFactor, dampTarget, deltaTime * 2.5f);
		speed *= streamDampFactor;
		float distance = speed * deltaTime;

		Vector3 position = transform.localPosition;
		position += direction * distance;
		transform.localPosition =
			grid.Wrapping ? WrapPosition(position) : ClampPosition(position);
		cameraViewDirty = true;
		// Pan does not change render tier — SetZoom already applies quality.
	}

	/// <summary>
	/// 以中距手感为基准，按当前摄像机 stick 距离等比放大/缩小平移速度，
	/// 避免远距看起来几乎挪不动。
	/// </summary>
	float GetDistanceScaledMoveSpeed()
	{
		float refZoom = Mathf.Clamp(moveSpeedReferenceZoom, 0.2f, 0.9f);
		float refDistance = Mathf.Abs(
			Mathf.Lerp(stickMinZoom, stickMaxZoom, refZoom));
		float refSpeed = Mathf.Lerp(moveSpeedMinZoom, moveSpeedMaxZoom, refZoom);
		if (refDistance < 1f)
		{
			refDistance = 1f;
		}

		float cameraDistance = Mathf.Abs(stick.localPosition.z);
		if (cameraDistance < 1f)
		{
			cameraDistance = 1f;
		}

		return refSpeed * (cameraDistance / refDistance);
	}

	Vector3 ClampPosition(Vector3 position)
	{
		GetMapGroundSize(out float mapWidth, out float mapHeight);
		GetGroundViewHalfExtents(out float halfX, out float halfZ);

		if (halfX * 2f >= mapWidth)
		{
			position.x = mapWidth * 0.5f;
		}
		else
		{
			position.x = Mathf.Clamp(position.x, halfX, mapWidth - halfX);
		}

		if (halfZ * 2f >= mapHeight)
		{
			position.z = mapHeight * 0.5f;
		}
		else
		{
			position.z = Mathf.Clamp(position.z, halfZ, mapHeight - halfZ);
		}

		return position;
	}

	Vector3 WrapPosition(Vector3 position)
	{
		float width = grid.CellCountX * HexMetrics.innerDiameter;
		while (position.x < 0f)
		{
			position.x += width;
		}
		while (position.x > width)
		{
			position.x -= width;
		}

		GetMapGroundSize(out _, out float mapHeight);
		GetGroundViewHalfExtents(out _, out float halfZ);
		if (halfZ * 2f >= mapHeight)
		{
			position.z = mapHeight * 0.5f;
		}
		else
		{
			position.z = Mathf.Clamp(position.z, halfZ, mapHeight - halfZ);
		}

		if (!grid.IsOverviewMode)
		{
			grid.CenterMap(position.x);
		}
		return position;
	}
}
