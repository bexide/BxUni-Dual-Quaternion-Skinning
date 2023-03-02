// 2023-02-06 BeXide,Inc. by Y.Hayashi
// original from https://github.com/KosRud/DQ-skinning-for-Unity

#ifndef UNIVERSAL_VERT_DQ_INCLUDED
#define UNIVERSAL_VERT_DQ_INCLUDED

#ifdef _DQ_SKINNING_ON

// variables used for DQ skining, always the same for every pass
TEXTURE2D(skinned_data_1);      SAMPLER(sampler_skinned_data_1);
TEXTURE2D(skinned_data_2);      SAMPLER(sampler_skinned_data_2);
TEXTURE2D(skinned_data_3);      SAMPLER(sampler_skinned_data_3);

uint skinned_tex_height;
uint skinned_tex_width;

#define Tex2DLod(data, uv, lod) SAMPLE_TEXTURE2D_LOD(data, sampler_ ## data, uv, lod)

// the actual skinning function
// don't change the code but change the argument type to the name of vertex input structure used in current pass
// for this pass it is VertexInputSkinningForward
void vert_DQ(inout Attributes v)
{
    float2 skinned_tex_uv;

    skinned_tex_uv.x = (float(v.id % skinned_tex_width)) / skinned_tex_width;
    skinned_tex_uv.y = (float(v.id / skinned_tex_width)) / skinned_tex_height;

    float4 data_1 = Tex2DLod(skinned_data_1, skinned_tex_uv, 0);
    #ifdef _NORMAL_TO_WORLD
    float4 data_2 = Tex2DLod(skinned_data_2, skinned_tex_uv, 0);
    #endif
    #ifdef _TANGENT_TO_WORLD        
    float2 data_3 = Tex2DLod(skinned_data_3, skinned_tex_uv, 0).xy;
    #endif

    v.positionOS.xyz = data_1.xyz;
    v.positionOS.w = 1;

    #ifdef _NORMAL_TO_WORLD
    v.normalOS.x = data_1.w;
    v.normalOS.yz = data_2.xy;
    #endif

    #ifdef _TANGENT_TO_WORLD        
    v.tangentOS.xy = data_2.zw;
    v.tangentOS.zw = data_3.xy;
    #endif
}
#else
#define vert_DQ(v)
#endif

#endif
