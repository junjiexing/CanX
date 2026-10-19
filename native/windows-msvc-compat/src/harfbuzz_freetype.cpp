#include <harfbuzz/hb-ft.h>
#include <harfbuzz/hb-ot.h>

#include <cstdlib>
#include <cstdint>

#include <ft2build.h>
#include FT_TRUETYPE_TABLES_H

namespace {

hb_user_data_key_t ft_face_key;

void destroy_ft_face(void* data)
{
  if (data != nullptr) {
    FT_Done_Face(static_cast<FT_Face>(data));
  }
}

hb_blob_t* reference_ft_table(hb_face_t*, hb_tag_t tag, void* user_data)
{
  auto face = static_cast<FT_Face>(user_data);
  FT_ULong length = 0;
  if (FT_Load_Sfnt_Table(face, tag, 0, nullptr, &length) != FT_Err_Ok ||
      length == 0) {
    return hb_blob_get_empty();
  }

  auto* bytes = static_cast<unsigned char*>(std::malloc(length));
  if (bytes == nullptr) {
    return hb_blob_get_empty();
  }
  if (FT_Load_Sfnt_Table(face, tag, 0, bytes, &length) != FT_Err_Ok) {
    std::free(bytes);
    return hb_blob_get_empty();
  }

  return hb_blob_create(reinterpret_cast<const char*>(bytes), length,
                        HB_MEMORY_MODE_WRITABLE, bytes, std::free);
}

} // namespace

extern "C" hb_font_t* hb_ft_font_create_referenced(FT_Face ft_face)
{
  if (ft_face == nullptr || FT_Reference_Face(ft_face) != FT_Err_Ok) {
    return hb_font_get_empty();
  }

  hb_face_t* face = hb_face_create_for_tables(reference_ft_table, ft_face,
                                               destroy_ft_face);
  hb_face_set_index(face, static_cast<unsigned int>(ft_face->face_index));
  if (ft_face->units_per_EM != 0) {
    hb_face_set_upem(face, ft_face->units_per_EM);
  }

  hb_font_t* font = hb_font_create(face);
  hb_face_destroy(face);
  hb_ot_font_set_funcs(font);
  hb_font_set_user_data(font, &ft_face_key, ft_face, nullptr, true);
  hb_ft_font_changed(font);
  return font;
}

extern "C" void hb_ft_font_changed(hb_font_t* font)
{
  auto face = static_cast<FT_Face>(hb_font_get_user_data(font, &ft_face_key));
  if (face == nullptr || face->size == nullptr) {
    return;
  }

  const auto upem = static_cast<uint64_t>(face->units_per_EM);
  const int x_scale = static_cast<int>(
      (static_cast<uint64_t>(face->size->metrics.x_scale) * upem + (1u << 15)) >> 16);
  const int y_scale = static_cast<int>(
      (static_cast<uint64_t>(face->size->metrics.y_scale) * upem + (1u << 15)) >> 16);
  hb_font_set_scale(font, x_scale, y_scale);
}
