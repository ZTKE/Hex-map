using UnityEngine;

using UnityEngine.Rendering.Universal;

/// <summary>
/// Component that controls the singleton camera that navigates the hex map.
/// </summary>
public class HexMapCamera : MonoBehaviour
{
	[SerializeField]
	float stickMinZoom, stickMaxZoom;

	[SerializeField]
	float swivelMinZoom, swivelMaxZoom;

	[SerializeField]
	float moveSpeedMinZoom, moveSpeedMaxZoom;

	[SerializeField]
	float rotationSpeed;

	[SerializeField, Range(0.5f, 0.9f)]
	float overviewZoomThreshold = 0.72f;

	[Tooltip("When disabled, every zoom level renders real streamed chunks. " +
		"The high-altitude overview is not shown as a fallback.")]
	[SerializeField]
	bool useHighAltitudeOverview;

	[Tooltip("Enable the continuous ocean when zoom is at or below this value.")]
	[SerializeField, Range(0.15f, 0.85f)]
	float globalOceanEnableZoom = 0.62f;

	[Tooltip("Return to chunk water above this value. The gap prevents flicker.")]
	[SerializeField, Range(0.2f, 0.95f)]
	float globalOceanDisableZoom = 0.70f;

	[SerializeField, Min(0.01f)]
	float moveSmoothTime = 0.12f;

	[SerializeField, Min(100f)]
	float overviewMoveSpeed = 950f;

	[SerializeField]
	HexGrid grid;

	Transform swivel, stick;

	float zoom = 1f;

	float rotationAngle;
	Vector2 smoothedMoveInput;
	Vector2 moveInputVelocity;
	Camera mapCamera;
	UniversalAdditionalCameraData cameraData;
	bool defaultAllowMSAA;
	bool defaultAllowHDR;
	bool defaultOcclusionCulling;
	bool defaultRenderShadows;
	bool overviewRenderingMode;
	bool globalOceanRequested;

	static HexMapCamera instance;

	/// <summary>
	/// Whether the singleton camera controls are locked.
	/// </summary>
	public static bool Locked
	{
		set => instance.enabled = !value;
	}

	/// <summary>
	/// Validate the position of the singleton camera.
	/// </summary>
	public static void ValidatePosition() => instance.AdjustPosition(0f, 0f);

	/// <summary>
	/// Put the startup view over the central Atlantic/Africa portion of a world
	/// map instead of inheriting the tiny editor map's south-west corner.
	/// </summary>
	public static void FocusWorldMap()
	{
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
		ValidatePosition();
	}

	void OnDisable()
	{
		smoothedMoveInput = Vector2.zero;
		moveInputVelocity = Vector2.zero;
	}

	void OnDestroy() => ApplyOverviewRenderingMode(false, true);

	void Update()
	{
		float zoomDelta = Input.GetAxis("Mouse ScrollWheel");
		if (zoomDelta != 0f)
		{
			AdjustZoom(zoomDelta);
		}

		float rotationDelta = Input.GetAxis("Rotation");
		if (rotationDelta != 0f)
		{
			AdjustRotation(rotationDelta);
		}

		Vector2 targetMoveInput = Vector2.ClampMagnitude(
			new Vector2(
				Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical")),
			1f);
		float effectiveSmoothTime = moveSmoothTime > 0.01f ?
			moveSmoothTime : 0.12f;
		smoothedMoveInput = Vector2.SmoothDamp(
			smoothedMoveInput, targetMoveInput, ref moveInputVelocity,
			effectiveSmoothTime, Mathf.Infinity, Time.deltaTime);
		if (smoothedMoveInput.sqrMagnitude > 0.0001f)
		{
			AdjustPosition(smoothedMoveInput.x, smoothedMoveInput.y);
		}
		else if (targetMoveInput == Vector2.zero)
		{
			smoothedMoveInput = Vector2.zero;
			moveInputVelocity = Vector2.zero;
		}
	}

	void AdjustZoom(float delta)
	{
		SetZoom(zoom + delta);
	}

	void SetZoom(float value)
	{
		zoom = Mathf.Clamp01(value);
		float effectiveOverviewThreshold = overviewZoomThreshold >= 0.5f ?
			overviewZoomThreshold : 0.72f;

		float distance = Mathf.Lerp(stickMinZoom, stickMaxZoom, zoom);
		stick.localPosition = new Vector3(0f, 0f, distance);

		float angle = Mathf.Lerp(swivelMinZoom, swivelMaxZoom, zoom);
		swivel.localRotation = Quaternion.Euler(angle, 0f, 0f);
		bool requestOverview =
			useHighAltitudeOverview && zoom < effectiveOverviewThreshold;
		grid.UpdateCameraView(
			transform.position,
			requestOverview,
			GetRequestedStreamingRadii(),
			EvaluateGlobalOceanRequest(requestOverview));
		ApplyOverviewRenderingMode(grid.IsOverviewMode);
	}

	bool EvaluateGlobalOceanRequest(bool requestOverview)
	{
		if (requestOverview)
		{
			globalOceanRequested = false;
			return false;
		}

		// Code fallbacks also cover a live editor object restored from an older
		// Enter Play Mode backup that predates these serialized fields.
		float enableZoom = globalOceanEnableZoom >= 0.3f ?
			Mathf.Clamp(globalOceanEnableZoom, 0.15f, 0.85f) : 0.62f;
		float configuredDisable = globalOceanDisableZoom > enableZoom ?
			globalOceanDisableZoom : 0.70f;
		float disableZoom = Mathf.Clamp(
			Mathf.Max(configuredDisable, enableZoom + 0.03f),
			enableZoom + 0.03f, 0.95f);
		globalOceanRequested = globalOceanRequested ?
			zoom < disableZoom : zoom <= enableZoom;
		return globalOceanRequested;
	}

	void ApplyOverviewRenderingMode(bool overview, bool force = false)
	{
		if (!mapCamera || (!force && overviewRenderingMode == overview))
		{
			return;
		}
		overviewRenderingMode = overview;
		mapCamera.allowMSAA = overview ? false : defaultAllowMSAA;
		mapCamera.allowHDR = overview ? false : defaultAllowHDR;
		mapCamera.useOcclusionCulling =
			overview ? false : defaultOcclusionCulling;
		if (cameraData)
		{
			cameraData.renderShadows = overview ? false : defaultRenderShadows;
		}
	}

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
		return new Vector2Int(
			Mathf.Clamp(
				Mathf.CeilToInt(halfHorizontalView / chunkWidth) + 2,
				1, 36),
			Mathf.Clamp(
				Mathf.CeilToInt(halfVerticalView / chunkHeight) + 2,
				1, 36));
	}

	void AdjustRotation (float delta)
	{
		rotationAngle += delta * rotationSpeed * Time.deltaTime;
		if (rotationAngle < 0f)
		{
			rotationAngle += 360f;
		}
		else if (rotationAngle >= 360f)
		{
			rotationAngle -= 360f;
		}
		transform.localRotation = Quaternion.Euler(0f, rotationAngle, 0f);
	}

	void AdjustPosition(float xDelta, float zDelta)
	{
		Vector3 localInput = Vector3.ClampMagnitude(
			new Vector3(xDelta, 0f, zDelta), 1f);
		Vector3 direction = transform.localRotation * localInput;
		float speed = Mathf.Lerp(moveSpeedMinZoom, moveSpeedMaxZoom, zoom);
		if (grid.IsOverviewMode)
		{
			float effectiveOverviewSpeed = overviewMoveSpeed >= 100f ?
				overviewMoveSpeed : 950f;
			speed = Mathf.Min(speed, effectiveOverviewSpeed);
		}
		float distance = speed * Time.deltaTime;

		Vector3 position = transform.localPosition;
		position += direction * distance;
		transform.localPosition =
			grid.Wrapping ? WrapPosition(position) : ClampPosition(position);
		grid.UpdateCameraView(
			transform.position,
			useHighAltitudeOverview && zoom <
				(overviewZoomThreshold >= 0.5f ?
					overviewZoomThreshold : 0.72f),
			GetRequestedStreamingRadii(),
			EvaluateGlobalOceanRequest(
				useHighAltitudeOverview && zoom <
					(overviewZoomThreshold >= 0.5f ?
						overviewZoomThreshold : 0.72f)));
	}

	Vector3 ClampPosition(Vector3 position)
	{
		float xMax = (grid.CellCountX - 0.5f) * HexMetrics.innerDiameter;
		position.x = Mathf.Clamp(position.x, 0f, xMax);

		float zMax = (grid.CellCountZ - 1) * (1.5f * HexMetrics.outerRadius);
		position.z = Mathf.Clamp(position.z, 0f, zMax);

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

		float zMax = (grid.CellCountZ - 1) * (1.5f * HexMetrics.outerRadius);
		position.z = Mathf.Clamp(position.z, 0f, zMax);

		if (!grid.IsOverviewMode)
		{
			grid.CenterMap(position.x);
		}
		return position;
	}
}
