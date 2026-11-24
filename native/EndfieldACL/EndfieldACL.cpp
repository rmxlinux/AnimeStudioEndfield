#include <cstdint>
#include <cstring>
#include <limits>
#include <new>
#include <vector>

#include "acl/core/ansi_allocator.h"
#include "acl/core/compressed_tracks.h"
#include "acl/core/iallocator.h"
#include "acl/core/track_writer.h"
#include "acl/decompression/decompress.h"
#include "acl/decompression/decompression_settings.h"
#include "rtm/quatf.h"
#include "rtm/scalarf.h"
#include "rtm/vector4f.h"

#define EF_API extern "C" __declspec(dllexport)

struct EFClip {
    float* values;
    int values_count;
    float* times;
    int times_count;
    int error;
};

struct TransformWriter : acl::track_writer {
    float* values;
    uint32_t stride;
    uint32_t sample_index = 0;

    TransformWriter(float* values, uint32_t stride) : values(values), stride(stride) {}

    void RTM_SIMD_CALL write_rotation(uint32_t index, rtm::quatf_arg0 value) {
        float* out = values + sample_index * stride + index * 10;
        out[0] = rtm::quat_get_x(value);
        out[1] = rtm::quat_get_y(value);
        out[2] = rtm::quat_get_z(value);
        out[3] = rtm::quat_get_w(value);
    }

    void RTM_SIMD_CALL write_translation(uint32_t index, rtm::vector4f_arg0 value) {
        float* out = values + sample_index * stride + index * 10 + 4;
        out[0] = rtm::vector_get_x(value);
        out[1] = rtm::vector_get_y(value);
        out[2] = rtm::vector_get_z(value);
    }

    void RTM_SIMD_CALL write_scale(uint32_t index, rtm::vector4f_arg0 value) {
        float* out = values + sample_index * stride + index * 10 + 7;
        out[0] = rtm::vector_get_x(value);
        out[1] = rtm::vector_get_y(value);
        out[2] = rtm::vector_get_z(value);
    }
};

struct ScalarWriter : acl::track_writer {
    float* values;
    uint32_t stride;
    uint32_t sample_index = 0;

    ScalarWriter(float* values, uint32_t stride) : values(values), stride(stride) {}

    void RTM_SIMD_CALL write_float1(uint32_t index, rtm::scalarf_arg0 value) {
        values[sample_index * stride + index] = rtm::scalar_cast(value);
    }
};

template<class Settings, class Writer>
static int decompress(const uint8_t* data, int length, EFClip* output, bool transform) {
    if (output == nullptr) return 1;
    *output = {};
    if (data == nullptr || length < 32) return output->error = 1;

    uint32_t serialized_size;
    std::memcpy(&serialized_size, data, sizeof(serialized_size));
    if (serialized_size < 32 || serialized_size > uint32_t(length)) return output->error = 1;

    // ACL reads SIMD data from a 16-byte aligned compressed_tracks buffer.
    std::vector<uint8_t> storage(size_t(length) + 15);
    auto* aligned = reinterpret_cast<uint8_t*>((reinterpret_cast<uintptr_t>(storage.data()) + 15) & ~uintptr_t(15));
    std::memcpy(aligned, data, size_t(length));

    acl::error_result error;
    const auto* tracks = acl::make_compressed_tracks(aligned, &error);
    if (tracks == nullptr || error.any()) return output->error = 2;
    if (tracks->get_track_type() != (transform ? acl::track_type8::qvvf : acl::track_type8::float1f))
        return output->error = 3;

    const uint32_t track_count = tracks->get_num_tracks();
    const uint32_t sample_count = tracks->get_num_samples_per_track();
    const uint64_t stride = uint64_t(track_count) * (transform ? 10 : 1);
    const uint64_t value_count = stride * sample_count;
    if (sample_count == 0 || stride == 0 || value_count > std::numeric_limits<int>::max())
        return output->error = 4;

    output->values_count = int(value_count);
    output->times_count = int(sample_count);
    output->values = new (std::nothrow) float[value_count]();
    output->times = new (std::nothrow) float[sample_count];
    if (output->values == nullptr || output->times == nullptr) return output->error = 5;

    acl::ansi_allocator allocator;
    auto* context = acl::make_decompression_context<Settings>(allocator);
    if (context == nullptr || !context->initialize(*tracks)) {
        if (context != nullptr) acl::deallocate_type(allocator, context);
        return output->error = 6;
    }

    Writer writer(output->values, uint32_t(stride));
    const float rate = tracks->get_sample_rate();
    for (uint32_t sample = 0; sample < sample_count; ++sample) {
        writer.sample_index = sample;
        const float time = rate > 0.0f ? float(sample) / rate : 0.0f;
        output->times[sample] = time;
        context->seek(time, acl::sample_rounding_policy::none);
        context->decompress_tracks(writer);
    }

    acl::deallocate_type(allocator, context);
    return 0;
}

EF_API int EndfieldACLDecompressTransform(const uint8_t* data, int length, EFClip* output) {
    return decompress<acl::default_transform_decompression_settings, TransformWriter>(data, length, output, true);
}

EF_API int EndfieldACLDecompressScalar(const uint8_t* data, int length, EFClip* output) {
    return decompress<acl::default_scalar_decompression_settings, ScalarWriter>(data, length, output, false);
}

EF_API void EndfieldACLDispose(EFClip* output) {
    if (output == nullptr) return;
    delete[] output->values;
    delete[] output->times;
    *output = {};
}
