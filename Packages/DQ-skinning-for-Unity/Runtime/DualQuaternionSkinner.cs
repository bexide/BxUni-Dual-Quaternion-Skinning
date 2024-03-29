﻿using System.Diagnostics.CodeAnalysis;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Replaces Unity's default linear skinning with DQ skinning
/// Add this component to a <a class="bold" href="https://docs.unity3d.com/ScriptReference/GameObject.html">GameObject</a>
/// that has <a class="bold" href="https://docs.unity3d.com/ScriptReference/SkinnedMeshRenderer.html">SkinnedMeshRenderer</a>
/// attached.<br />
/// Do not remove <a class="bold" href="https://docs.unity3d.com/ScriptReference/SkinnedMeshRenderer.html">SkinnedMeshRenderer</a>
/// component!<br />
/// Make sure that all materials of the animated object are using shader \"
/// <b>MadCake/Material/Standard hacked for DQ skinning</b>\"
/// </summary>
[RequireComponent(typeof(MeshFilter))]
public class DualQuaternionSkinner : MonoBehaviour
{
    /// <summary>
    /// Bone orientation is required for bulge-compensation.<br />
    /// Do not set directly, use custom editor instead.
    /// </summary>
    public Vector3 m_boneOrientationVector = Vector3.up;

    private bool m_viewFrustumCulling = true;

    [SuppressMessage("ReSharper", "NotAccessedField.Local")]
    private struct VertexInfo
    {
        // could use float3 instead of float4 but NVidia says structures not aligned to 128 bits are slow
        // https://developer.nvidia.com/content/understanding-structured-buffer-performance

        public Vector4 position;
        public Vector4 normal;
        public Vector4 tangent;

        public int boneIndex0;
        public int boneIndex1;
        public int boneIndex2;
        public int boneIndex3;

        public float weight0;
        public float weight1;
        public float weight2;
        public float weight3;

        public float compensation_coef;
    }

    [SuppressMessage("ReSharper", "NotAccessedField.Local")]
    private struct MorphDelta
    {
        // could use float3 instead of float4 but NVidia says structures not aligned to 128 bits are slow
        // https://developer.nvidia.com/content/understanding-structured-buffer-performance

        public Vector4 position;
        public Vector4 normal;
        public Vector4 tangent;
    }

    [SuppressMessage("ReSharper", "NotAccessedField.Local")]
    private struct DualQuaternion
    {
        public Quaternion rotationQuaternion;
        public Vector4    position;
    }

    private const int k_numThreads   = 128;  // must be same in compute shader code (DQ.cginc)
    private const int k_textureWidth = 1024; // no need to adjust compute shaders

    /// <summary>
    /// Adjusts the amount of bulge-compensation.
    /// </summary>
    [Range(0, 1)]
    public float m_bulgeCompensation;

    public ComputeShader m_shaderComputeBoneDq;
    public ComputeShader m_shaderDqBlend;
    public ComputeShader m_shaderApplyMorph;

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (m_shaderComputeBoneDq == null)
        {
            m_shaderComputeBoneDq = FindAssetOfType<ComputeShader>("ComputeBoneDQ");
        }
        if (m_shaderDqBlend == null)
        {
            m_shaderDqBlend = FindAssetOfType<ComputeShader>("DQBlend");
        }
        if (m_shaderApplyMorph == null)
        {
            m_shaderApplyMorph = FindAssetOfType<ComputeShader>("ApplyMorph");
        }
    }

    private T FindAssetOfType<T>(string filter) where T : UnityEngine.Object
    {
        string[] guids = AssetDatabase.FindAssets(filter);
        if (guids.Length <= 0) { return null; }
        string path = AssetDatabase.GUIDToAssetPath(guids[0]);
        var    obj  = AssetDatabase.LoadAssetAtPath<T>(path);
        return obj;
    }
#endif

    /// <summary>
    /// Indicates whether DualQuaternionSkinner is currently active.
    /// </summary>
    public bool Started { get; private set; }

    private Matrix4x4[] m_poseMatrices;

    private ComputeBuffer m_bufPoseMatrices;
    private ComputeBuffer m_bufSkinnedDq;
    private ComputeBuffer m_bufBindDq;

    private ComputeBuffer m_bufVertInfo;
    private ComputeBuffer m_bufMorphTemp1;
    private ComputeBuffer m_bufMorphTemp2;

    private ComputeBuffer m_bufBoneDirections;

    private ComputeBuffer[] m_arrBufMorphDeltas;

    private float[] m_morphWeights;

    private MeshFilter MeshFilter
    {
        get
        {
            if (m_meshFilter == null) { m_meshFilter = GetComponent<MeshFilter>(); }

            return m_meshFilter;
        }
    }

    private MeshFilter m_meshFilter;

    private MeshRenderer MeshRenderer
    {
        get
        {
            if (m_meshRenderer == null)
            {
                m_meshRenderer = GetComponent<MeshRenderer>();
                if (m_meshRenderer == null)
                {
                    m_meshRenderer = gameObject.AddComponent<MeshRenderer>();
                }
            }

            return m_meshRenderer;
        }
    }

    private MeshRenderer m_meshRenderer;

    private SkinnedMeshRenderer SkinnedMeshRenderer
    {
        get
        {
            if (m_skinnedMeshRenderer == null)
            {
                m_skinnedMeshRenderer = GetComponent<SkinnedMeshRenderer>();
            }

            return m_skinnedMeshRenderer;
        }
    }

    private SkinnedMeshRenderer m_skinnedMeshRenderer;

    private MaterialPropertyBlock m_materialPropertyBlock;

    private Transform[] m_bones;
    private Matrix4x4[] m_bindPoses;

    /*
        Vulkan and OpenGL only support ComputeBuffer in compute shaders
        passing data to the vertex and fragment shaders is done through RenderTextures

        using ComputeBuffers would improve the efficiency slightly but it would only work with Dx11

        layout is as such:
            rtSkinnedData_1			float4			vertex.xyz,	normal.x
            rtSkinnedData_2			float4			normal.yz,	tangent.xy
            rtSkinnedData_3			float2			tangent.zw
    */
    private RenderTexture m_rtSkinnedData1;
    private RenderTexture m_rtSkinnedData2;
    private RenderTexture m_rtSkinnedData3;

    private int m_kernelHandleComputeBoneDq;
    private int m_kernelHandleDqBlend;
    private int m_kernelHandleApplyMorph;

    /// <summary>
    /// Enable or disable view frustum culling.<br />
    /// When moving the bones manually to test the script, bounding box does not update (it is pre-calculated based on available animations), which may lead to improper culling.
    /// </summary>
    public void SetViewFrustumCulling(bool viewFrustrumculling)
    {
        if (m_viewFrustumCulling == viewFrustrumculling) { return; }

        m_viewFrustumCulling = viewFrustrumculling;

        if (Started) { UpdateViewFrustumCulling(); }
    }

    /// <summary>
    /// Returns current state of view frustrum culling.
    /// </summary>
    /// <returns>Current state of view frustrum culling (true = enabled, false = disabled)</returns>
    public bool GetViewFrustumCulling()
    {
        return m_viewFrustumCulling;
    }

    private void UpdateViewFrustumCulling()
    {
        MeshFilter.mesh.bounds = m_viewFrustumCulling
            ? SkinnedMeshRenderer.localBounds
            : new Bounds(Vector3.zero, Vector3.one * 100000000);
    }

    /// <summary>
    /// Returns an array of currently applied blend shape weights.<br />
    /// Default range is 0-100.<br />
    /// It is possible to apply negative weights or exceeding 100.
    /// </summary>
    /// <returns>Array of currently applied blend shape weights</returns>
    public float[] GetBlendShapeWeights()
    {
        float[] weights = new float[m_morphWeights.Length];
        for (int i = 0; i < weights.Length; i++) { weights[i] = m_morphWeights[i]; }

        return weights;
    }

    /// <summary>
    /// Applies blend shape weights from the given array.<br />
    /// Default range is 0-100.<br />
    /// It is possible to apply negative weights or exceeding 100.
    /// </summary>
    /// <param name="weights">An array of weights to be applied</param>
    public void SetBlendShapeWeights(float[] weights)
    {
        if (weights.Length != m_morphWeights.Length)
        {
            throw new System.ArgumentException(
                "An array of weights must contain the number of elements " +
                $"equal to the number of available blendShapes. Currently " +
                $"{m_morphWeights.Length} blendShapes ara available but {weights.Length} weights were passed.");
        }

        for (int i = 0; i < weights.Length; i++) { m_morphWeights[i] = weights[i]; }

        ApplyMorphs();
    }

    /// <summary>
    /// Set weight for the blend shape with given index.<br />
    /// Default range is 0-100.<br />
    /// It is possible to apply negative weights or exceeding 100.
    /// </summary>
    /// <param name="index">Index of the blend shape</param>
    /// <param name="weight">Weight to be applied</param>
    public void SetBlendShapeWeight(int index, float weight)
    {
        if (Started == false)
        {
            GetComponent<SkinnedMeshRenderer>().SetBlendShapeWeight(index, weight);
            return;
        }

        if (index < 0 || index >= m_morphWeights.Length)
        {
            throw new System.IndexOutOfRangeException("Blend shape index out of range");
        }

        m_morphWeights[index] = weight;

        ApplyMorphs();
    }

    /// <summary>
    /// Returns currently applied weight for the blend shape with given index.<br />
    /// Default range is 0-100.<br />
    /// It is possible to apply negative weights or exceeding 100.
    /// </summary>
    /// <param name="index">Index of the blend shape</param>
    /// <returns>Currently applied weight</returns>
    public float GetBlendShapeWeight(int index)
    {
        if (Started == false)
        {
            return GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(index);
        }

        if (index < 0 || index >= m_morphWeights.Length)
        {
            throw new System.IndexOutOfRangeException("Blend shape index out of range");
        }

        return m_morphWeights[index];
    }

    /// <summary>
    /// UnityEngine.<a href="https://docs.unity3d.com/ScriptReference/Mesh.html">Mesh</a> that is currently being rendered.
    /// @see <a href="https://docs.unity3d.com/ScriptReference/Mesh.GetBlendShapeName.html">Mesh.GetBlendShapeName(int shapeIndex)</a>
    /// @see <a href="https://docs.unity3d.com/ScriptReference/Mesh.GetBlendShapeIndex.html">Mesh.GetBlendShapeIndex(string blendShapeName)</a>
    /// @see <a href="https://docs.unity3d.com/ScriptReference/Mesh-blendShapeCount.html">Mesh.blendShapeCount</a>
    /// </summary>
    public Mesh Mesh
    {
        get
        {
            if (Started == false) { return SkinnedMeshRenderer.sharedMesh; }

            return MeshFilter.mesh;
        }
    }

    /// <summary>
    /// If the value of boneOrientationVector was changed while DualQuaternionSkinner is active (started == true), UpdatePerVertexCompensationCoef() must be called in order for the change to take effect.
    /// </summary>
    public void UpdatePerVertexCompensationCoef()
    {
        if (Started == false) { return; }

        int vertexCount = MeshFilter.mesh.vertexCount;
        int numData     = GetAlignedDataCount(vertexCount);
        var vertInfos   = new VertexInfo[numData];
        m_bufVertInfo.GetData(vertInfos);

        for (int i = 0; i < vertexCount; i++)
        {
            var bindPose         = m_bindPoses[vertInfos[i].boneIndex0].inverse;
            var boneBindRotation = bindPose.rotation;
            var boneDirection
                = boneBindRotation *
                  m_boneOrientationVector; // ToDo figure out bone orientation
            var bonePosition = bindPose.GetPosition();
            var toBone       = bonePosition - (Vector3)vertInfos[i].position;

            vertInfos[i].compensation_coef = Vector3.Cross(toBone, boneDirection).magnitude;
        }

        m_bufVertInfo.SetData(vertInfos);
        ApplyMorphs();
    }

    // calculate group number for processing given data
    private int GetNumGroups(int dataCount)
    {
        return (dataCount - 1) % k_numThreads + 1;
    }

    // calculate data number aligned around thread number
    private int GetAlignedDataCount(int dataCount)
    {
        return GetNumGroups(dataCount) * k_numThreads;
    }

    // calculate texture height enough for given vertices
    private int GetVertexTextureHeight(int vertexCount)
    {
        return (vertexCount - 1) / k_textureWidth + 1;
    }

    private void GrabMeshFromSkinnedMeshRenderer()
    {
        ReleaseBuffers();

        var mesh = SkinnedMeshRenderer.sharedMesh;
        MeshFilter.mesh = mesh;

        m_bindPoses = mesh.bindposes;

        int blendShapeCount = mesh.blendShapeCount;
        m_arrBufMorphDeltas = new ComputeBuffer[blendShapeCount];

        m_morphWeights = new float[blendShapeCount];

        int vertexCount   = mesh.vertexCount;
        var deltaVertices = new Vector3[vertexCount];
        var deltaNormals  = new Vector3[vertexCount];
        var deltaTangents = new Vector3[vertexCount];

        int numVertInfos   = GetAlignedDataCount(vertexCount);
        var deltaVertInfos = new MorphDelta[numVertInfos];

        for (int i = 0; i < blendShapeCount; i++)
        {
            mesh.GetBlendShapeFrameVertices(i, 0, deltaVertices, deltaNormals, deltaTangents);

            m_arrBufMorphDeltas[i] = new ComputeBuffer(numVertInfos, sizeof(float) * 12);

            for (int k = 0; k < vertexCount; k++)
            {
                deltaVertInfos[k].position = deltaVertices[k];
                deltaVertInfos[k].normal   = deltaNormals[k];
                deltaVertInfos[k].tangent  = deltaTangents[k];
            }

            m_arrBufMorphDeltas[i].SetData(deltaVertInfos);
        }

        MeshRenderer.materials = SkinnedMeshRenderer.sharedMaterials;
        var materials = MeshRenderer.materials;
        foreach (var m in materials) { m.EnableKeyword("_DQ_SKINNING_ON"); }

        m_shaderDqBlend.SetInt("textureWidth", k_textureWidth);

        m_poseMatrices = new Matrix4x4[mesh.bindposes.Length];

        // initiate textures and buffers

        int textureHeight = GetVertexTextureHeight(mesh.vertexCount);

        m_rtSkinnedData1
            = new RenderTexture(k_textureWidth, textureHeight, 0, RenderTextureFormat.ARGBFloat)
            {
                filterMode        = FilterMode.Point,
                enableRandomWrite = true
            };
        m_rtSkinnedData1.Create();
        m_shaderDqBlend.SetTexture(m_kernelHandleDqBlend, "skinned_data_1", m_rtSkinnedData1);

        m_rtSkinnedData2
            = new RenderTexture(k_textureWidth, textureHeight, 0, RenderTextureFormat.ARGBFloat)
            {
                filterMode        = FilterMode.Point,
                enableRandomWrite = true
            };
        m_rtSkinnedData2.Create();
        m_shaderDqBlend.SetTexture(m_kernelHandleDqBlend, "skinned_data_2", m_rtSkinnedData2);

        m_rtSkinnedData3
            = new RenderTexture(k_textureWidth, textureHeight, 0, RenderTextureFormat.RGFloat)
            {
                filterMode        = FilterMode.Point,
                enableRandomWrite = true
            };
        m_rtSkinnedData3.Create();
        m_shaderDqBlend.SetTexture(m_kernelHandleDqBlend, "skinned_data_3", m_rtSkinnedData3);

        int numBindPosesData = GetAlignedDataCount(mesh.bindposes.Length);
        m_bufPoseMatrices = new ComputeBuffer(numBindPosesData, sizeof(float) * 16);
        m_shaderComputeBoneDq.SetBuffer(
            m_kernelHandleComputeBoneDq,
            "pose_matrices",
            m_bufPoseMatrices);

        m_bufSkinnedDq = new ComputeBuffer(numBindPosesData, sizeof(float) * 8);
        m_shaderComputeBoneDq.SetBuffer(
            m_kernelHandleComputeBoneDq,
            "skinned_dual_quaternions",
            m_bufSkinnedDq);
        m_shaderDqBlend.SetBuffer(
            m_kernelHandleDqBlend,
            "skinned_dual_quaternions",
            m_bufSkinnedDq);

        m_bufBoneDirections = new ComputeBuffer(numBindPosesData, sizeof(float) * 4);
        m_shaderComputeBoneDq.SetBuffer(
            m_kernelHandleComputeBoneDq,
            "bone_directions",
            m_bufBoneDirections);
        m_shaderDqBlend.SetBuffer(
            m_kernelHandleDqBlend,
            "bone_directions",
            m_bufBoneDirections);

        m_bufVertInfo = new ComputeBuffer(
            numVertInfos,
            sizeof(float) * 16 + sizeof(int) * 4 + sizeof(float));
        var vertInfos   = new VertexInfo[numVertInfos];
        var vertices    = mesh.vertices;
        var normals     = mesh.normals;
        var tangents    = mesh.tangents;
        var boneWeights = mesh.boneWeights;
        for (int i = 0; i < vertexCount; i++)
        {
            vertInfos[i].position = vertices[i];

            vertInfos[i].boneIndex0 = boneWeights[i].boneIndex0;
            vertInfos[i].boneIndex1 = boneWeights[i].boneIndex1;
            vertInfos[i].boneIndex2 = boneWeights[i].boneIndex2;
            vertInfos[i].boneIndex3 = boneWeights[i].boneIndex3;

            vertInfos[i].weight0 = boneWeights[i].weight0;
            vertInfos[i].weight1 = boneWeights[i].weight1;
            vertInfos[i].weight2 = boneWeights[i].weight2;
            vertInfos[i].weight3 = boneWeights[i].weight3;

            // determine per-vertex compensation coef

            var bindPose         = m_bindPoses[vertInfos[i].boneIndex0].inverse;
            var boneBindRotation = bindPose.rotation;
            var boneDirection
                = boneBindRotation *
                  m_boneOrientationVector; // ToDo figure out bone orientation
            var bonePosition = bindPose.GetPosition();
            var toBone       = bonePosition - (Vector3)vertInfos[i].position;

            vertInfos[i].compensation_coef = Vector3.Cross(toBone, boneDirection).magnitude;
        }

        if (normals.Length > 0)
        {
            for (int i = 0; i < mesh.vertexCount; i++) { vertInfos[i].normal = normals[i]; }
        }

        if (tangents.Length > 0)
        {
            for (int i = 0; i < mesh.vertexCount; i++) { vertInfos[i].tangent = tangents[i]; }
        }

        m_bufVertInfo.SetData(vertInfos);
        m_shaderDqBlend.SetBuffer(m_kernelHandleDqBlend, "vertex_infos", m_bufVertInfo);

        m_bufMorphTemp1 = new ComputeBuffer(
            numVertInfos,
            sizeof(float) * 16 + sizeof(int) * 4 + sizeof(float));
        m_bufMorphTemp2 = new ComputeBuffer(
            numVertInfos,
            sizeof(float) * 16 + sizeof(int) * 4 + sizeof(float));

        // bind DQ buffer

        var bindDqs = new DualQuaternion[numBindPosesData];
        for (int i = 0; i < m_bindPoses.Length; i++)
        {
            bindDqs[i].rotationQuaternion = m_bindPoses[i].rotation;
            bindDqs[i].position           = m_bindPoses[i].GetPosition();
        }

        m_bufBindDq = new ComputeBuffer(bindDqs.Length, sizeof(float) * 8);
        m_bufBindDq.SetData(bindDqs);
        m_shaderComputeBoneDq.SetBuffer(
            m_kernelHandleComputeBoneDq,
            "bind_dual_quaternions",
            m_bufBindDq);

        UpdateViewFrustumCulling();
        ApplyMorphs();
    }

    private void ApplyMorphs()
    {
        var bufMorphedVertexInfos = GetMorphedVertexInfos(
            m_bufVertInfo,
            ref m_bufMorphTemp1,
            ref m_bufMorphTemp2,
            m_arrBufMorphDeltas,
            m_morphWeights);

        m_shaderDqBlend.SetBuffer(m_kernelHandleDqBlend, "vertex_infos", bufMorphedVertexInfos);
    }

    private ComputeBuffer GetMorphedVertexInfos(
        ComputeBuffer     bufOriginal,
        ref ComputeBuffer bufTemp_1,
        ref ComputeBuffer bufTemp_2,
        ComputeBuffer[]   arrBufDelta,
        float[]           weights)
    {
        var bufSource = bufOriginal;

        for (int i = 0; i < weights.Length; i++)
        {
            if (weights[i] == 0) { continue; }

            if (arrBufDelta[i] == null) { throw new System.NullReferenceException(); }

            m_shaderApplyMorph.SetBuffer(m_kernelHandleApplyMorph, "source", bufSource);
            m_shaderApplyMorph.SetBuffer(m_kernelHandleApplyMorph, "target", bufTemp_1);
            m_shaderApplyMorph.SetBuffer(m_kernelHandleApplyMorph, "delta", arrBufDelta[i]);
            m_shaderApplyMorph.SetFloat("weight", weights[i] / 100f);

            int numThreadGroups = GetNumGroups(bufSource.count);
            m_shaderApplyMorph.Dispatch(m_kernelHandleApplyMorph, numThreadGroups, 1, 1);

            bufSource = bufTemp_1;
            bufTemp_1 = bufTemp_2;
            bufTemp_2 = bufSource;
        }

        return bufSource;
    }

    private void ReleaseBuffers()
    {
        m_bufBindDq?.Release();
        m_bufPoseMatrices?.Release();
        m_bufSkinnedDq?.Release();

        m_bufVertInfo?.Release();
        m_bufMorphTemp1?.Release();
        m_bufMorphTemp2?.Release();

        m_bufBoneDirections?.Release();

        if (m_arrBufMorphDeltas != null)
        {
            foreach (var buf in m_arrBufMorphDeltas) { buf?.Release(); }
        }
    }

    private void OnDestroy()
    {
        ReleaseBuffers();
    }

    // Use this for initialization
    private void Start()
    {
        m_materialPropertyBlock = new MaterialPropertyBlock();

        m_shaderComputeBoneDq = Instantiate(m_shaderComputeBoneDq); // bug workaround
        m_shaderDqBlend       = Instantiate(m_shaderDqBlend);       // bug workaround
        m_shaderApplyMorph    = Instantiate(m_shaderApplyMorph);    // bug workaround

        m_kernelHandleComputeBoneDq = m_shaderComputeBoneDq.FindKernel("CSMain");
        m_kernelHandleDqBlend       = m_shaderDqBlend.FindKernel("CSMain");
        m_kernelHandleApplyMorph    = m_shaderApplyMorph.FindKernel("CSMain");

        m_bones = SkinnedMeshRenderer.bones;

        Started = true;
        GrabMeshFromSkinnedMeshRenderer();

        for (int i = 0; i < m_morphWeights.Length; i++)
        {
            m_morphWeights[i] = SkinnedMeshRenderer.GetBlendShapeWeight(i);
        }

        SkinnedMeshRenderer.enabled = false;

#if UNITY_EDITOR
        EditorApplication.playModeStateChanged += OnEditorPlayModeChanged;
#endif
    }

#if UNITY_EDITOR
    private void Stop()
    {
        Started = false;
        ReleaseBuffers();
    }
#endif

    // Update is called once per frame
    private void LateUpdate()
    {
        if (!Started) { return; }

        // update bounds
        MeshRenderer.bounds = GetWorldBounds(
            MeshFilter.mesh.bounds,
            SkinnedMeshRenderer.rootBone.localToWorldMatrix);

        if (MeshRenderer.isVisible == false) { return; }

        MeshFilter.mesh.MarkDynamic(); // once or every frame? idk.
        // at least it does not affect performance

        for (int i = 0; i < m_bones.Length; i++)
        {
            m_poseMatrices[i] = m_bones[i].localToWorldMatrix;
        }

        m_bufPoseMatrices.SetData(m_poseMatrices);

        // Calculate blended quaternions

        int numThreadGroups = GetNumGroups(m_bones.Length);
        m_shaderComputeBoneDq.SetVector("boneOrientation", m_boneOrientationVector);
        m_shaderComputeBoneDq.SetMatrix("self_matrix", transform.worldToLocalMatrix);
        m_shaderComputeBoneDq.Dispatch(m_kernelHandleComputeBoneDq, numThreadGroups, 1, 1);

        numThreadGroups = GetNumGroups(MeshFilter.mesh.vertexCount);
        m_shaderDqBlend.SetFloat("compensation_coef", m_bulgeCompensation);
        m_shaderDqBlend.Dispatch(m_kernelHandleDqBlend, numThreadGroups, 1, 1);

        m_materialPropertyBlock.SetTexture("skinned_data_1", m_rtSkinnedData1);
        m_materialPropertyBlock.SetTexture("skinned_data_2", m_rtSkinnedData2);
        m_materialPropertyBlock.SetTexture("skinned_data_3", m_rtSkinnedData3);

        int textureHeight = GetVertexTextureHeight(MeshFilter.mesh.vertexCount);
        m_materialPropertyBlock.SetInt("skinned_tex_height", textureHeight);
        m_materialPropertyBlock.SetInt("skinned_tex_width", k_textureWidth);

        MeshRenderer.SetPropertyBlock(m_materialPropertyBlock);
    }

    private Bounds GetWorldBounds(Bounds localBounds, Matrix4x4 localToWorldMatrix)
    {
        var bounds = new Bounds();
        var min    = localBounds.min;
        var max    = localBounds.max;
        bounds.Encapsulate(localToWorldMatrix.MultiplyPoint(new Vector3(min.x, min.y, min.z)));
        bounds.Encapsulate(localToWorldMatrix.MultiplyPoint(new Vector3(min.x, max.y, min.z)));
        bounds.Encapsulate(localToWorldMatrix.MultiplyPoint(new Vector3(max.x, max.y, min.z)));
        bounds.Encapsulate(localToWorldMatrix.MultiplyPoint(new Vector3(min.x, min.y, max.z)));
        bounds.Encapsulate(localToWorldMatrix.MultiplyPoint(new Vector3(max.x, min.y, max.z)));
        bounds.Encapsulate(localToWorldMatrix.MultiplyPoint(new Vector3(min.x, max.y, max.z)));
        bounds.Encapsulate(localToWorldMatrix.MultiplyPoint(new Vector3(max.x, max.y, max.z)));
        return bounds;
    }

#if UNITY_EDITOR
    private void OnEditorPlayModeChanged(PlayModeStateChange change)
    {
        if (change == PlayModeStateChange.ExitingPlayMode)
        {
            EditorApplication.playModeStateChanged -= OnEditorPlayModeChanged;
            Stop();
        }
    }
#endif
}
