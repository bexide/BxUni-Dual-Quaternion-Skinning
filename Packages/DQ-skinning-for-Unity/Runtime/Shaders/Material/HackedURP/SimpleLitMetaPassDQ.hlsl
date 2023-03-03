#ifndef UNIVERSAL_SIMPLE_LIT_META_PASS_DQ_INCLUDED
#define UNIVERSAL_SIMPLE_LIT_META_PASS_DQ_INCLUDED

#include "UniversalMetaPassDQ.hlsl"

half4 UniversalFragmentMetaSimple(Varyings input) : SV_Target
{
    float2 uv = input.uv;
    MetaInput metaInput;
    metaInput.Albedo = _BaseColor.rgb * SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv).rgb;
    metaInput.Emission = SampleEmission(uv, _EmissionColor.rgb, TEXTURE2D_ARGS(_EmissionMap, sampler_EmissionMap));

    return UniversalFragmentMeta(input, metaInput);
}
#endif
