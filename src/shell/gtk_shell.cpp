#include "shell/gtk_shell.hpp"

#include <cairo.h>
#include <gdk/gdk.h>
#if defined(GDK_WINDOWING_X11)
#include <gdk/gdkx.h>
#ifdef None
#undef None
#endif
#ifdef Status
#undef Status
#endif
#ifdef Success
#undef Success
#endif
#endif
#include <gtk/gtk.h>

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <memory>
#include <optional>
#include <string>
#include <vector>

#include "core/motion.hpp"
#include "core/qr.hpp"
#include "core/reduce.hpp"
#include "core/shell_placement.hpp"
#include "core/snapshot.hpp"
#include "core/tray_pixmap.hpp"
#include "core/vocabulary.hpp"

namespace adbhawk::shell {

namespace {

int64_t NowMs() {
  struct timespec ts {};
  clock_gettime(CLOCK_BOOTTIME, &ts);
  return static_cast<int64_t>(ts.tv_sec) * 1000 + (ts.tv_nsec / 1'000'000);
}

std::string ResolveThemeVariant(const std::string& base_theme, bool want_dark) {
  if (base_theme.empty()) {
    return want_dark ? "Adwaita-dark" : "Adwaita";
  }
  std::string lower;
  lower.reserve(base_theme.size());
  for (char c : base_theme) {
    lower.push_back(static_cast<char>(std::tolower(static_cast<unsigned char>(c))));
  }

  bool is_currently_dark = (lower.find("dark") != std::string::npos);
  if (want_dark == is_currently_dark) {
    return base_theme;
  }

  const char* home = g_get_home_dir();
  std::string home_dir = (home != nullptr) ? std::string(home) : "";

  auto theme_dir_exists = [&](const std::string& name) {
    return g_file_test(("/usr/share/themes/" + name).c_str(), G_FILE_TEST_IS_DIR) ||
           (!home_dir.empty() &&
            g_file_test((home_dir + "/.themes/" + name).c_str(), G_FILE_TEST_IS_DIR));
  };

  if (!want_dark) {
    size_t pos = base_theme.find("Dark");
    if (pos == std::string::npos) pos = base_theme.find("dark");
    if (pos != std::string::npos) {
      std::string cand = base_theme;
      cand.replace(pos, 4, (base_theme[pos] == 'D') ? "Light" : "light");
      if (theme_dir_exists(cand)) return cand;

      if (pos > 0 && base_theme[pos - 1] == '-') {
        std::string stripped = base_theme.substr(0, pos - 1) + base_theme.substr(pos + 4);
        if (theme_dir_exists(stripped)) return stripped;
      }
    }
    if (theme_dir_exists("WhiteSur-Light")) return "WhiteSur-Light";
    if (theme_dir_exists("Mint-Y")) return "Mint-Y";
    return "Adwaita";
  } else {
    size_t pos = base_theme.find("Light");
    if (pos == std::string::npos) pos = base_theme.find("light");
    if (pos != std::string::npos) {
      std::string cand = base_theme;
      cand.replace(pos, 5, (base_theme[pos] == 'L') ? "Dark" : "dark");
      if (theme_dir_exists(cand)) return cand;
    }
    std::string cand = base_theme + "-Dark";
    if (theme_dir_exists(cand)) return cand;
    cand = base_theme + "-dark";
    if (theme_dir_exists(cand)) return cand;

    if (theme_dir_exists("WhiteSur-Dark")) return "WhiteSur-Dark";
    if (theme_dir_exists("Mint-Y-Dark")) return "Mint-Y-Dark";
    return "Adwaita-dark";
  }
}

class GtkShellImpl : public IShell {
 public:
  explicit GtkShellImpl(ShellCallbacks callbacks)
      : callbacks_(std::move(callbacks)) {
    GtkSettings* settings = gtk_settings_get_default();
    if (settings != nullptr) {
      gchar* cur_theme = nullptr;
      g_object_get(settings, "gtk-theme-name", &cur_theme, nullptr);
      if (cur_theme != nullptr) {
        system_theme_name_ = cur_theme;
        g_free(cur_theme);
      }
    }
    BuildFlyoutWindow();
    BuildBubbleWindow();
  }

  ~GtkShellImpl() override {
    StopAnim();
    if (bubble_window_ != nullptr) {
      gtk_widget_destroy(bubble_window_);
      bubble_window_ = nullptr;
    }
    if (flyout_window_ != nullptr) {
      gtk_widget_destroy(flyout_window_);
      flyout_window_ = nullptr;
    }
  }

  void StopAnim() {
    if (anim_tick_id_ != 0 && flyout_window_ != nullptr) {
      gtk_widget_remove_tick_callback(flyout_window_, anim_tick_id_);
      anim_tick_id_ = 0;
    }
    anim_kind_ = SurfaceAnimKind::None;
  }

  void RestoreBubble() {
    if (bubble_window_ != nullptr && bubble_enabled_) {
      UpdateBubblePlacement();
      gtk_widget_show_all(bubble_window_);
      bubble_visible_ = true;
    }
  }

  void PresentFlyoutWindow() {
    gtk_widget_show_all(flyout_window_);
    GdkWindow* gdk_win = gtk_widget_get_window(flyout_window_);
#if defined(GDK_WINDOWING_X11)
    if (gdk_win != nullptr && GDK_IS_X11_WINDOW(gdk_win)) {
      guint32 server_time = gdk_x11_get_server_time(gdk_win);
      gtk_window_present_with_time(GTK_WINDOW(flyout_window_), server_time);
      gdk_window_focus(gdk_win, server_time);
      Display* xdisplay = GDK_WINDOW_XDISPLAY(gdk_win);
      Window xid = GDK_WINDOW_XID(gdk_win);
      GdkDisplay* gdk_disp = gdk_window_get_display(gdk_win);
      if (xdisplay != nullptr && xid != 0 && gdk_disp != nullptr) {
        gdk_x11_display_error_trap_push(gdk_disp);
        XSetInputFocus(xdisplay, xid, RevertToParent, CurrentTime);
        gdk_x11_display_error_trap_pop_ignored(gdk_disp);
      }
      return;
    }
#endif
    gtk_window_present(GTK_WINDOW(flyout_window_));
  }

  void ShowFlyout(core::ScreenPoint hint) override {
    UpdatePlacement(hint);
    flyout_visible_ = true;

    // §41.2: Hide bubble while flyout is visible
    if (bubble_window_ != nullptr && bubble_visible_) {
      gtk_widget_hide(bubble_window_);
    }

    if (IsReducedMotion()) {
      StopAnim();
      gtk_widget_set_opacity(flyout_window_, 1.0);
      gtk_window_move(GTK_WINDOW(flyout_window_), target_anchor_x_, target_anchor_y_);
      PresentFlyoutWindow();
      return;
    }

    if (anim_kind_ == SurfaceAnimKind::Entering) {
      PresentFlyoutWindow();
      return;
    }

    if (anim_kind_ == SurfaceAnimKind::Exiting) {
      // Retarget smoothly from current position
      anim_kind_ = SurfaceAnimKind::Entering;
      anim_start_progress_ = current_anim_progress_;
      anim_start_time_us_ = 0;
      PresentFlyoutWindow();
      return;
    }

    anim_kind_ = SurfaceAnimKind::Entering;
    anim_start_progress_ = 0.0;
    current_anim_progress_ = 0.0;
    anim_start_time_us_ = 0;

    int start_y = target_anchor_y_ + anim_offset_y_;
    gtk_widget_set_opacity(flyout_window_, 0.0);
    gtk_window_move(GTK_WINDOW(flyout_window_), target_anchor_x_, start_y);
    PresentFlyoutWindow();

    if (anim_tick_id_ == 0) {
      anim_tick_id_ = gtk_widget_add_tick_callback(
          flyout_window_, OnSurfaceAnimTickStatic, this, nullptr);
    }
  }

  void HideFlyout() override {
    if (!flyout_visible_ && anim_kind_ == SurfaceAnimKind::None) {
      return;
    }
    last_deactivate_hide_ms_ = NowMs();

    if (IsReducedMotion()) {
      StopAnim();
      gtk_widget_hide(flyout_window_);
      gtk_widget_set_opacity(flyout_window_, 1.0);
      flyout_visible_ = false;
      RestoreBubble();
      return;
    }

    if (anim_kind_ == SurfaceAnimKind::Exiting) {
      return;
    }

    if (anim_kind_ == SurfaceAnimKind::Entering) {
      // Retarget from current progress
      anim_kind_ = SurfaceAnimKind::Exiting;
      anim_start_progress_ = 1.0 - current_anim_progress_;
      current_anim_progress_ = anim_start_progress_;
      anim_start_time_us_ = 0;
      return;
    }

    anim_kind_ = SurfaceAnimKind::Exiting;
    anim_start_progress_ = 0.0;
    current_anim_progress_ = 0.0;
    anim_start_time_us_ = 0;

    if (anim_tick_id_ == 0) {
      anim_tick_id_ = gtk_widget_add_tick_callback(
          flyout_window_, OnSurfaceAnimTickStatic, this, nullptr);
    }
  }

  void ToggleFlyout(core::ScreenPoint hint) override {
    int64_t now = NowMs();
    // F4: Debounce toggle within 250ms of focus-out deactivation hide
    if (core::ShouldDebounceTrayToggle(last_deactivate_hide_ms_, now, 250)) {
      return;
    }
    if (flyout_visible_ && anim_kind_ != SurfaceAnimKind::Exiting) {
      HideFlyout();
    } else {
      ShowFlyout(hint);
    }
  }

  bool IsFlyoutVisible() const override { return flyout_visible_; }

  void ShowBubble() override {
    bubble_enabled_ = true;
    if (!flyout_visible_ && bubble_window_ != nullptr) {
      UpdateBubblePlacement();
      gtk_widget_show_all(bubble_window_);
      bubble_visible_ = true;
    }
  }

  void HideBubble() override {
    bubble_enabled_ = false;
    if (bubble_window_ != nullptr) {
      gtk_widget_hide(bubble_window_);
      bubble_visible_ = false;
    }
  }

  bool IsBubbleVisible() const override { return bubble_visible_; }

  void UpdateSnapshot(const core::AppSnapshot& snapshot) override {
    snapshot_ = snapshot;
    RefreshUiFromSnapshot();
  }

  void UpdateTheme(platform::ThemePreference theme) override {
    theme_ = theme;
    GtkSettings* settings = gtk_settings_get_default();
    if (settings != nullptr) {
      if (system_theme_name_.empty()) {
        gchar* cur_theme = nullptr;
        g_object_get(settings, "gtk-theme-name", &cur_theme, nullptr);
        if (cur_theme != nullptr) {
          system_theme_name_ = cur_theme;
          g_free(cur_theme);
        }
      }

      std::string target_theme = system_theme_name_;
      gboolean prefer_dark = FALSE;

      if (theme == platform::ThemePreference::Light) {
        target_theme = ResolveThemeVariant(system_theme_name_, false);
        prefer_dark = FALSE;
      } else if (theme == platform::ThemePreference::Dark) {
        target_theme = ResolveThemeVariant(system_theme_name_, true);
        prefer_dark = TRUE;
      } else {
        target_theme = system_theme_name_;
        std::string lower;
        for (char c : target_theme) {
          lower.push_back(static_cast<char>(std::tolower(static_cast<unsigned char>(c))));
        }
        prefer_dark = (lower.find("dark") != std::string::npos);
      }

      if (!target_theme.empty()) {
        g_object_set(settings, "gtk-theme-name", target_theme.c_str(),
                     "gtk-application-prefer-dark-theme", prefer_dark,
                     nullptr);
      }
    }
    if (bubble_drawing_area_ != nullptr) {
      gtk_widget_queue_draw(bubble_drawing_area_);
    }
    if (flyout_window_ != nullptr) {
      gtk_widget_reset_style(flyout_window_);
      gtk_widget_queue_draw(flyout_window_);
    }
  }

 private:
  void BuildFlyoutWindow() {
    flyout_window_ = gtk_window_new(GTK_WINDOW_TOPLEVEL);
    gtk_window_set_title(GTK_WINDOW(flyout_window_), "ADB Hawk");
    gtk_window_set_default_size(GTK_WINDOW(flyout_window_), 240, 130);
    gtk_widget_set_size_request(flyout_window_, 240, -1);
    gtk_window_set_resizable(GTK_WINDOW(flyout_window_), FALSE);
    gtk_window_set_decorated(GTK_WINDOW(flyout_window_), FALSE);

    // F6: UTILITY type hint signals tiling WMs (i3/sway) to float the window
    gtk_window_set_type_hint(GTK_WINDOW(flyout_window_),
                             GDK_WINDOW_TYPE_HINT_UTILITY);
    gtk_window_set_skip_taskbar_hint(GTK_WINDOW(flyout_window_), TRUE);
    gtk_window_set_skip_pager_hint(GTK_WINDOW(flyout_window_), TRUE);
    gtk_window_set_keep_above(GTK_WINDOW(flyout_window_), TRUE);
    gtk_widget_realize(flyout_window_);

    // Light dismiss on focus loss (when unpinned)
    g_signal_connect(flyout_window_, "focus-out-event",
                     G_CALLBACK(OnFocusOutStatic), this);
    g_signal_connect(flyout_window_, "key-press-event",
                     G_CALLBACK(OnKeyPressStatic), this);
    g_signal_connect(flyout_window_, "delete-event",
                     G_CALLBACK(OnDeleteStatic), this);
    g_signal_connect(flyout_window_, "window-state-event",
                     G_CALLBACK(+[](GtkWidget*, GdkEventWindowState* event, gpointer data) -> gboolean {
                       auto* self = static_cast<GtkShellImpl*>(data);
                       if (event->new_window_state & GDK_WINDOW_STATE_ICONIFIED) {
                         self->flyout_visible_ = false;
                         if (self->bubble_window_ != nullptr && self->bubble_enabled_) {
                           self->UpdateBubblePlacement();
                           gtk_widget_show_all(self->bubble_window_);
                           self->bubble_visible_ = true;
                         }
                       }
                       return FALSE;
                     }),
                     this);
    g_signal_connect(flyout_window_, "size-allocate",
                     G_CALLBACK(+[](GtkWidget*, GdkRectangle* alloc, gpointer data) {
                       auto* self = static_cast<GtkShellImpl*>(data);
                       if (self->last_allocated_w_ != alloc->width ||
                           self->last_allocated_h_ != alloc->height) {
                         self->last_allocated_w_ = alloc->width;
                         self->last_allocated_h_ = alloc->height;
                         self->ReanchorFlyout(alloc->width, alloc->height);
                       }
                     }),
                     this);

    GtkWidget* main_vbox = gtk_box_new(GTK_ORIENTATION_VERTICAL, 0);
    gtk_container_add(GTK_CONTAINER(flyout_window_), main_vbox);

    // 1. Header Bar
    GtkWidget* header_box = gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 8);
    gtk_widget_set_margin_start(header_box, 12);
    gtk_widget_set_margin_end(header_box, 12);
    gtk_widget_set_margin_top(header_box, 10);
    gtk_widget_set_margin_bottom(header_box, 6);

    back_button_ = gtk_button_new_with_label("‹ Back");
    gtk_widget_set_no_show_all(back_button_, TRUE);
    g_signal_connect(back_button_, "clicked",
                     G_CALLBACK(+[](GtkButton*, gpointer data) {
                       static_cast<GtkShellImpl*>(data)->NavigateToPage(0);
                     }),
                     this);
    gtk_box_pack_start(GTK_BOX(header_box), back_button_, FALSE, FALSE, 0);

    GtkWidget* title_label = gtk_label_new("ADB Hawk");
    PangoAttrList* attrs = pango_attr_list_new();
    pango_attr_list_insert(attrs, pango_attr_weight_new(PANGO_WEIGHT_BOLD));
    gtk_label_set_attributes(GTK_LABEL(title_label), attrs);
    pango_attr_list_unref(attrs);
    gtk_label_set_xalign(GTK_LABEL(title_label), 0.0f);
    gtk_box_pack_start(GTK_BOX(header_box), title_label, TRUE, TRUE, 0);

    pin_button_ = gtk_toggle_button_new_with_label("📌");
    gtk_widget_set_tooltip_text(pin_button_, "Pin window");
    g_signal_connect(pin_button_, "toggled",
                     G_CALLBACK(+[](GtkToggleButton* btn, gpointer data) {
                       auto* self = static_cast<GtkShellImpl*>(data);
                       self->is_pinned_ = gtk_toggle_button_get_active(btn);
                       if (!self->is_pinned_) {
                         int win_w = 240;
                         int win_h = 130;
                         if (gtk_widget_get_realized(self->flyout_window_)) {
                           win_w = gtk_widget_get_allocated_width(self->flyout_window_);
                           win_h = gtk_widget_get_allocated_height(self->flyout_window_);
                         }
                         self->RepositionFlyout(win_w, win_h);
                       }
                       if (self->callbacks_.on_toggle_pin) {
                         self->callbacks_.on_toggle_pin(self->is_pinned_);
                       }
                     }),
                     this);
    gtk_box_pack_end(GTK_BOX(header_box), pin_button_, FALSE, FALSE, 0);

    GtkWidget* header_event_box = gtk_event_box_new();
    gtk_event_box_set_visible_window(GTK_EVENT_BOX(header_event_box), FALSE);
    gtk_container_add(GTK_CONTAINER(header_event_box), header_box);
    g_signal_connect(header_event_box, "button-press-event",
                     G_CALLBACK(+[](GtkWidget*, GdkEventButton* event, gpointer data) -> gboolean {
                       auto* self = static_cast<GtkShellImpl*>(data);
                       if (event->button == GDK_BUTTON_PRIMARY) {
                         gtk_window_begin_move_drag(GTK_WINDOW(self->flyout_window_),
                                                    static_cast<gint>(event->button),
                                                    static_cast<gint>(event->x_root),
                                                    static_cast<gint>(event->y_root),
                                                    event->time);
                         return TRUE;
                       }
                       return FALSE;
                     }),
                     this);

    gtk_box_pack_start(GTK_BOX(main_vbox), header_event_box, FALSE, FALSE, 0);

    // Separator
    gtk_box_pack_start(GTK_BOX(main_vbox),
                       gtk_separator_new(GTK_ORIENTATION_HORIZONTAL), FALSE,
                       FALSE, 0);

    // 2. Navigation Stack
    stack_ = gtk_stack_new();
    gtk_stack_set_transition_type(GTK_STACK(stack_),
                                  GTK_STACK_TRANSITION_TYPE_SLIDE_LEFT_RIGHT);
    gtk_stack_set_transition_duration(GTK_STACK(stack_), 167);  // §41.3
    gtk_stack_set_homogeneous(GTK_STACK(stack_), FALSE);
    gtk_stack_set_vhomogeneous(GTK_STACK(stack_), FALSE);
    gtk_stack_set_hhomogeneous(GTK_STACK(stack_), FALSE);

    // Page 0: Devices List
    GtkWidget* page0_box = gtk_box_new(GTK_ORIENTATION_VERTICAL, 8);
    gtk_widget_set_margin_start(page0_box, 12);
    gtk_widget_set_margin_end(page0_box, 12);
    gtk_widget_set_margin_top(page0_box, 6);
    gtk_widget_set_margin_bottom(page0_box, 8);

    // Outage Banner
    outage_box_ = gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 8);
    gtk_widget_set_no_show_all(outage_box_, TRUE);
    outage_label_ = gtk_label_new("");
    gtk_label_set_line_wrap(GTK_LABEL(outage_label_), TRUE);
    gtk_label_set_line_wrap_mode(GTK_LABEL(outage_label_), PANGO_WRAP_WORD);
    gtk_label_set_max_width_chars(GTK_LABEL(outage_label_), 28);
    gtk_label_set_xalign(GTK_LABEL(outage_label_), 0.0f);
    gtk_box_pack_start(GTK_BOX(outage_box_), outage_label_, TRUE, TRUE, 0);
    outage_restart_btn_ = gtk_button_new_with_label("Restart ADB");
    g_signal_connect(outage_restart_btn_, "clicked",
                     G_CALLBACK(+[](GtkButton*, gpointer data) {
                       auto* self = static_cast<GtkShellImpl*>(data);
                       if (self->callbacks_.on_restart_adb) {
                         self->callbacks_.on_restart_adb();
                       }
                     }),
                     this);
    gtk_box_pack_end(GTK_BOX(outage_box_), outage_restart_btn_, FALSE, FALSE, 0);
    gtk_box_pack_start(GTK_BOX(page0_box), outage_box_, FALSE, FALSE, 0);

    // Summary count label
    summary_label_ = gtk_label_new("No devices");
    gtk_label_set_xalign(GTK_LABEL(summary_label_), 0.0f);
    gtk_box_pack_start(GTK_BOX(page0_box), summary_label_, FALSE, FALSE, 0);

    // Scrolled device list container
    GtkWidget* scrolled = gtk_scrolled_window_new(nullptr, nullptr);
    gtk_scrolled_window_set_policy(GTK_SCROLLED_WINDOW(scrolled),
                                   GTK_POLICY_NEVER, GTK_POLICY_AUTOMATIC);
    gtk_scrolled_window_set_propagate_natural_height(GTK_SCROLLED_WINDOW(scrolled), TRUE);
    gtk_scrolled_window_set_max_content_height(GTK_SCROLLED_WINDOW(scrolled), 200);
    device_list_box_ = gtk_box_new(GTK_ORIENTATION_VERTICAL, 6);
    gtk_container_add(GTK_CONTAINER(scrolled), device_list_box_);
    gtk_box_pack_start(GTK_BOX(page0_box), scrolled, TRUE, TRUE, 0);

    GtkWidget* pair_btn = gtk_button_new_with_label("+ Pair device with Wi-Fi");
    gtk_widget_set_margin_top(pair_btn, 4);
    g_signal_connect(pair_btn, "clicked",
                     G_CALLBACK(+[](GtkButton*, gpointer data) {
                       static_cast<GtkShellImpl*>(data)->ShowPairingPage();
                     }),
                     this);
    gtk_box_pack_end(GTK_BOX(page0_box), pair_btn, FALSE, FALSE, 0);

    gtk_stack_add_named(GTK_STACK(stack_), page0_box, "list");

    // Page 1: Device Details
    GtkWidget* page1_box = gtk_box_new(GTK_ORIENTATION_VERTICAL, 10);
    gtk_widget_set_valign(page1_box, GTK_ALIGN_START);
    gtk_widget_set_margin_start(page1_box, 12);
    gtk_widget_set_margin_end(page1_box, 12);
    gtk_widget_set_margin_top(page1_box, 10);
    gtk_widget_set_margin_bottom(page1_box, 10);

    details_name_label_ = gtk_label_new("");
    PangoAttrList* d_attrs = pango_attr_list_new();
    pango_attr_list_insert(d_attrs, pango_attr_weight_new(PANGO_WEIGHT_BOLD));
    pango_attr_list_insert(d_attrs, pango_attr_scale_new(1.2));
    gtk_label_set_attributes(GTK_LABEL(details_name_label_), d_attrs);
    pango_attr_list_unref(d_attrs);
    gtk_label_set_xalign(GTK_LABEL(details_name_label_), 0.0f);
    gtk_box_pack_start(GTK_BOX(page1_box), details_name_label_, FALSE, FALSE, 0);

    details_status_label_ = gtk_label_new("");
    gtk_label_set_xalign(GTK_LABEL(details_status_label_), 0.0f);
    gtk_box_pack_start(GTK_BOX(page1_box), details_status_label_, FALSE, FALSE, 0);

    details_guidance_box_ = gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 6);
    gtk_widget_set_no_show_all(details_guidance_box_, TRUE);
    details_guidance_label_ = gtk_label_new("");
    gtk_label_set_line_wrap(GTK_LABEL(details_guidance_label_), TRUE);
    gtk_label_set_line_wrap_mode(GTK_LABEL(details_guidance_label_), PANGO_WRAP_WORD);
    gtk_label_set_max_width_chars(GTK_LABEL(details_guidance_label_), 28);
    gtk_label_set_xalign(GTK_LABEL(details_guidance_label_), 0.0f);
    gtk_box_pack_start(GTK_BOX(details_guidance_box_), details_guidance_label_, TRUE, TRUE, 0);
    gtk_box_pack_start(GTK_BOX(page1_box), details_guidance_box_, FALSE, FALSE, 4);

    gtk_box_pack_start(GTK_BOX(page1_box),
                       gtk_separator_new(GTK_ORIENTATION_HORIZONTAL), FALSE,
                       FALSE, 4);

    details_card_box_ = gtk_box_new(GTK_ORIENTATION_VERTICAL, 6);
    gtk_widget_set_valign(details_card_box_, GTK_ALIGN_START);
    gtk_box_pack_start(GTK_BOX(page1_box), details_card_box_, FALSE, FALSE, 0);

    details_disconnect_btn_ = gtk_button_new_with_label("Disconnect");
    gtk_widget_set_no_show_all(details_disconnect_btn_, TRUE);
    g_signal_connect(details_disconnect_btn_, "clicked",
                     G_CALLBACK(+[](GtkButton*, gpointer data) {
                       auto* self = static_cast<GtkShellImpl*>(data);
                       if (self->callbacks_.on_disconnect_device &&
                           !self->selected_device_id_.empty()) {
                         self->callbacks_.on_disconnect_device(
                             self->selected_device_id_);
                         self->NavigateToPage(0);
                       }
                     }),
                     this);
    gtk_box_pack_start(GTK_BOX(page1_box), details_disconnect_btn_, FALSE, FALSE, 12);

    gtk_stack_add_named(GTK_STACK(stack_), page1_box, "details");

    // Page 2: Pairing
    GtkWidget* page2_box = gtk_box_new(GTK_ORIENTATION_VERTICAL, 8);
    gtk_widget_set_valign(page2_box, GTK_ALIGN_START);
    gtk_widget_set_margin_start(page2_box, 12);
    gtk_widget_set_margin_end(page2_box, 12);
    gtk_widget_set_margin_top(page2_box, 10);
    gtk_widget_set_margin_bottom(page2_box, 10);

    GtkWidget* pair_title = gtk_label_new("Pair with Wireless Debugging");
    PangoAttrList* p_attrs = pango_attr_list_new();
    pango_attr_list_insert(p_attrs, pango_attr_weight_new(PANGO_WEIGHT_BOLD));
    pango_attr_list_insert(p_attrs, pango_attr_scale_new(1.1));
    gtk_label_set_attributes(GTK_LABEL(pair_title), p_attrs);
    pango_attr_list_unref(p_attrs);
    gtk_label_set_xalign(GTK_LABEL(pair_title), 0.0f);
    gtk_box_pack_start(GTK_BOX(page2_box), pair_title, FALSE, FALSE, 0);

    GtkWidget* pair_hint = gtk_label_new(
        "On phone: Settings → Developer options → Wireless debugging → Pair device with pairing code");
    gtk_label_set_line_wrap(GTK_LABEL(pair_hint), TRUE);
    gtk_label_set_line_wrap_mode(GTK_LABEL(pair_hint), PANGO_WRAP_WORD);
    gtk_label_set_max_width_chars(GTK_LABEL(pair_hint), 28);
    gtk_label_set_xalign(GTK_LABEL(pair_hint), 0.0f);
    PangoAttrList* ph_attrs = pango_attr_list_new();
    pango_attr_list_insert(ph_attrs,
                           pango_attr_foreground_new(0x8888, 0x8888, 0x8888));
    gtk_label_set_attributes(GTK_LABEL(pair_hint), ph_attrs);
    pango_attr_list_unref(ph_attrs);
    gtk_box_pack_start(GTK_BOX(page2_box), pair_hint, FALSE, FALSE, 2);

    GtkWidget* host_lbl = gtk_label_new("IP address & port:");
    gtk_label_set_xalign(GTK_LABEL(host_lbl), 0.0f);
    gtk_box_pack_start(GTK_BOX(page2_box), host_lbl, FALSE, FALSE, 2);

    pairing_host_port_entry_ = gtk_entry_new();
    gtk_entry_set_placeholder_text(GTK_ENTRY(pairing_host_port_entry_),
                                   "192.168.1.50:37281");
    gtk_box_pack_start(GTK_BOX(page2_box), pairing_host_port_entry_, FALSE, FALSE, 0);

    GtkWidget* code_lbl = gtk_label_new("6-digit pairing code:");
    gtk_label_set_xalign(GTK_LABEL(code_lbl), 0.0f);
    gtk_box_pack_start(GTK_BOX(page2_box), code_lbl, FALSE, FALSE, 2);

    pairing_code_entry_ = gtk_entry_new();
    gtk_entry_set_max_length(GTK_ENTRY(pairing_code_entry_), 6);
    gtk_entry_set_placeholder_text(GTK_ENTRY(pairing_code_entry_), "123456");
    gtk_box_pack_start(GTK_BOX(page2_box), pairing_code_entry_, FALSE, FALSE, 0);

    pairing_status_label_ = gtk_label_new("");
    gtk_label_set_line_wrap(GTK_LABEL(pairing_status_label_), TRUE);
    gtk_label_set_line_wrap_mode(GTK_LABEL(pairing_status_label_), PANGO_WRAP_WORD);
    gtk_label_set_max_width_chars(GTK_LABEL(pairing_status_label_), 28);
    gtk_label_set_xalign(GTK_LABEL(pairing_status_label_), 0.0f);
    gtk_box_pack_start(GTK_BOX(page2_box), pairing_status_label_, FALSE, FALSE, 4);

    GtkWidget* action_box = gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 8);
    gtk_widget_set_margin_top(action_box, 6);

    pairing_cancel_btn_ = gtk_button_new_with_label("Cancel");
    g_signal_connect(pairing_cancel_btn_, "clicked",
                     G_CALLBACK(+[](GtkButton*, gpointer data) {
                       static_cast<GtkShellImpl*>(data)->NavigateToPage(0);
                     }),
                     this);
    gtk_box_pack_start(GTK_BOX(action_box), pairing_cancel_btn_, TRUE, TRUE, 0);

    pairing_submit_btn_ = gtk_button_new_with_label("Pair");
    g_signal_connect(pairing_submit_btn_, "clicked",
                     G_CALLBACK(+[](GtkButton*, gpointer data) {
                       static_cast<GtkShellImpl*>(data)->SubmitPairing();
                     }),
                     this);
    g_signal_connect(pairing_code_entry_, "activate",
                     G_CALLBACK(+[](GtkEntry*, gpointer data) {
                       static_cast<GtkShellImpl*>(data)->SubmitPairing();
                     }),
                     this);
    gtk_box_pack_start(GTK_BOX(action_box), pairing_submit_btn_, TRUE, TRUE, 0);

    gtk_box_pack_start(GTK_BOX(page2_box), action_box, FALSE, FALSE, 0);

    gtk_stack_add_named(GTK_STACK(stack_), page2_box, "pair");

    gtk_box_pack_start(GTK_BOX(main_vbox), stack_, TRUE, TRUE, 0);
  }

  void BuildBubbleWindow() {
    bubble_window_ = gtk_window_new(GTK_WINDOW_TOPLEVEL);
    gtk_window_set_default_size(GTK_WINDOW(bubble_window_), 18, 18);
    gtk_widget_set_size_request(bubble_window_, 18, 18);
    gtk_window_set_resizable(GTK_WINDOW(bubble_window_), FALSE);
    gtk_window_set_type_hint(GTK_WINDOW(bubble_window_),
                             GDK_WINDOW_TYPE_HINT_NOTIFICATION);
    gtk_window_set_keep_above(GTK_WINDOW(bubble_window_), TRUE);
    gtk_window_set_accept_focus(GTK_WINDOW(bubble_window_), FALSE);
    gtk_window_set_skip_taskbar_hint(GTK_WINDOW(bubble_window_), TRUE);
    gtk_window_set_skip_pager_hint(GTK_WINDOW(bubble_window_), TRUE);

    GdkScreen* screen = gtk_widget_get_screen(bubble_window_);
    GdkVisual* visual = gdk_screen_get_rgba_visual(screen);
    if (visual != nullptr && gdk_screen_is_composited(screen)) {
      gtk_widget_set_visual(bubble_window_, visual);
    }
    gtk_widget_set_app_paintable(bubble_window_, TRUE);

    GtkCssProvider* css = gtk_css_provider_new();
    gtk_css_provider_load_from_data(
        css,
        "window, eventbox, drawingarea { background-color: transparent; background: none; border: none; }",
        -1, nullptr);
    GtkStyleContext* ctx = gtk_widget_get_style_context(bubble_window_);
    gtk_style_context_add_provider(ctx, GTK_STYLE_PROVIDER(css),
                                   GTK_STYLE_PROVIDER_PRIORITY_APPLICATION);
    g_object_unref(css);

    g_signal_connect(bubble_window_, "draw",
                     G_CALLBACK(+[](GtkWidget*, cairo_t* cr, gpointer) -> gboolean {
                       cairo_set_operator(cr, CAIRO_OPERATOR_CLEAR);
                       cairo_paint(cr);
                       return FALSE;
                     }),
                     nullptr);

    GtkWidget* event_box = gtk_event_box_new();
    gtk_event_box_set_visible_window(GTK_EVENT_BOX(event_box), FALSE);
    gtk_container_add(GTK_CONTAINER(bubble_window_), event_box);

    bubble_drawing_area_ = gtk_drawing_area_new();
    gtk_widget_set_size_request(bubble_drawing_area_, 18, 18);
    gtk_container_add(GTK_CONTAINER(event_box), bubble_drawing_area_);
    g_signal_connect(bubble_drawing_area_, "draw",
                     G_CALLBACK(OnDrawBubbleStatic), this);

    g_signal_connect(event_box, "button-press-event",
                     G_CALLBACK(+[](GtkWidget*, GdkEventButton*, gpointer data) {
                       auto* self = static_cast<GtkShellImpl*>(data);
                       self->ToggleFlyout(core::ScreenPoint{0, 0});
                       return TRUE;
                     }),
                     this);
  }

  void NavigateToPage(int page_idx) {
    bool reduced = IsReducedMotion();
    guint dur = reduced ? 0 : 167;
    gtk_stack_set_transition_duration(GTK_STACK(stack_), dur);

    if (page_idx == 0) {
      gtk_stack_set_transition_type(GTK_STACK(stack_),
                                    GTK_STACK_TRANSITION_TYPE_SLIDE_RIGHT);
      gtk_widget_hide(back_button_);
      gtk_stack_set_visible_child_name(GTK_STACK(stack_), "list");
      if (pairing_code_entry_ != nullptr) {
        gtk_entry_set_text(GTK_ENTRY(pairing_code_entry_), "");
      }
      if (pairing_submit_btn_ != nullptr) {
        gtk_widget_set_sensitive(pairing_submit_btn_, TRUE);
      }
      if (pairing_cancel_btn_ != nullptr) {
        gtk_widget_set_sensitive(pairing_cancel_btn_, TRUE);
      }
    } else if (page_idx == 1) {
      gtk_stack_set_transition_type(GTK_STACK(stack_),
                                    GTK_STACK_TRANSITION_TYPE_SLIDE_LEFT);
      gtk_widget_show(back_button_);
      gtk_stack_set_visible_child_name(GTK_STACK(stack_), "details");
    } else if (page_idx == 2) {
      gtk_stack_set_transition_type(GTK_STACK(stack_),
                                    GTK_STACK_TRANSITION_TYPE_SLIDE_LEFT);
      gtk_widget_show(back_button_);
      gtk_stack_set_visible_child_name(GTK_STACK(stack_), "pair");
    }
    current_page_ = page_idx;
  }

  void RefreshUiFromSnapshot() {
    // 1. Update Outage Banner
    if (snapshot_.health.kind == core::GlobalHealthKind::AdbUnavailable ||
        snapshot_.health.kind == core::GlobalHealthKind::AdbRestoring) {
      std::string text = snapshot_.health.title.value_or("ADB Status");
      if (snapshot_.health.detail.has_value()) {
        text += "\n" + *snapshot_.health.detail;
      }
      gtk_label_set_text(GTK_LABEL(outage_label_), text.c_str());
      gtk_widget_show_all(outage_box_);
    } else {
      gtk_widget_hide(outage_box_);
    }

    // 2. Summary count label
    if (!snapshot_.devices.empty()) {
      if (snapshot_.device_count_label.has_value()) {
        gtk_label_set_text(GTK_LABEL(summary_label_),
                           snapshot_.device_count_label->c_str());
      } else {
        gtk_label_set_text(GTK_LABEL(summary_label_), "1 device connected");
      }
    } else {
      gtk_label_set_text(GTK_LABEL(summary_label_), "No devices connected");
    }

    // 3. Clear and rebuild device list box
    GList* children = gtk_container_get_children(GTK_CONTAINER(device_list_box_));
    for (GList* l = children; l != nullptr; l = l->next) {
      gtk_widget_destroy(GTK_WIDGET(l->data));
    }
    g_list_free(children);

    if (snapshot_.devices.empty()) {
      GtkWidget* empty_box = gtk_box_new(GTK_ORIENTATION_VERTICAL, 4);
      gtk_widget_set_margin_top(empty_box, 32);
      GtkWidget* empty_title = gtk_label_new("No devices found");
      PangoAttrList* e_attrs = pango_attr_list_new();
      pango_attr_list_insert(e_attrs, pango_attr_weight_new(PANGO_WEIGHT_BOLD));
      gtk_label_set_attributes(GTK_LABEL(empty_title), e_attrs);
      pango_attr_list_unref(e_attrs);
      gtk_box_pack_start(GTK_BOX(empty_box), empty_title, FALSE, FALSE, 0);

      GtkWidget* empty_desc = gtk_label_new(
          "Connect an Android device via USB or enable Wireless Debugging.");
      gtk_label_set_line_wrap(GTK_LABEL(empty_desc), TRUE);
      gtk_label_set_xalign(GTK_LABEL(empty_desc), 0.5f);
      gtk_box_pack_start(GTK_BOX(empty_box), empty_desc, FALSE, FALSE, 0);

      gtk_box_pack_start(GTK_BOX(device_list_box_), empty_box, TRUE, TRUE, 0);
    } else {
      for (const auto& dev : snapshot_.devices) {
        GtkWidget* btn = gtk_button_new();
        gtk_button_set_relief(GTK_BUTTON(btn), GTK_RELIEF_NONE);

        GtkWidget* row_box = gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 8);
        gtk_widget_set_margin_top(row_box, 6);
        gtk_widget_set_margin_bottom(row_box, 6);
        gtk_widget_set_margin_start(row_box, 8);
        gtk_widget_set_margin_end(row_box, 8);

        GtkWidget* text_box = gtk_box_new(GTK_ORIENTATION_VERTICAL, 2);
        GtkWidget* name_label = gtk_label_new(dev.display_name.c_str());
        PangoAttrList* n_attrs = pango_attr_list_new();
        pango_attr_list_insert(n_attrs, pango_attr_weight_new(PANGO_WEIGHT_BOLD));
        gtk_label_set_attributes(GTK_LABEL(name_label), n_attrs);
        pango_attr_list_unref(n_attrs);
        gtk_label_set_xalign(GTK_LABEL(name_label), 0.0f);
        gtk_box_pack_start(GTK_BOX(text_box), name_label, FALSE, FALSE, 0);

        std::string sub = dev.state_label;
        if (!dev.transport_label.empty()) {
          sub += " • " + dev.transport_label;
        }
        if (dev.battery_percent.has_value()) {
          sub += " • 🔋 " + std::to_string(*dev.battery_percent) + "%";
        }
        GtkWidget* sub_label = gtk_label_new(sub.c_str());
        gtk_label_set_xalign(GTK_LABEL(sub_label), 0.0f);
        gtk_box_pack_start(GTK_BOX(text_box), sub_label, FALSE, FALSE, 0);

        gtk_box_pack_start(GTK_BOX(row_box), text_box, TRUE, TRUE, 0);

        GtkWidget* arrow = gtk_label_new("›");
        gtk_box_pack_end(GTK_BOX(row_box), arrow, FALSE, FALSE, 0);

        gtk_container_add(GTK_CONTAINER(btn), row_box);

        g_object_set_data_full(G_OBJECT(btn), "dev_id",
                               g_strdup(dev.id.c_str()), g_free);
        g_object_set_data(G_OBJECT(btn), "shell_ptr", this);

        g_signal_connect(btn, "clicked",
                         G_CALLBACK(+[](GtkButton* b, gpointer) {
                           auto* self = static_cast<GtkShellImpl*>(
                               g_object_get_data(G_OBJECT(b), "shell_ptr"));
                           const char* did = static_cast<const char*>(
                               g_object_get_data(G_OBJECT(b), "dev_id"));
                           if (self != nullptr && did != nullptr) {
                             self->ShowDeviceDetails(did);
                           }
                         }),
                         nullptr);

        gtk_box_pack_start(GTK_BOX(device_list_box_), btn, FALSE, FALSE, 0);
      }
    }

    // 4. Pairing Offers
    for (const auto& offer : snapshot_.pairing_offers) {
      GtkWidget* offer_btn = gtk_button_new();
      GtkWidget* o_box = gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 8);
      gtk_widget_set_margin_top(o_box, 6);
      gtk_widget_set_margin_bottom(o_box, 6);
      gtk_widget_set_margin_start(o_box, 8);
      gtk_widget_set_margin_end(o_box, 8);

      GtkWidget* o_label = gtk_label_new(
          ("Pairing available: " + offer.display_name).c_str());
      gtk_box_pack_start(GTK_BOX(o_box), o_label, TRUE, TRUE, 0);

      GtkWidget* pair_now = gtk_label_new("Pair ›");
      gtk_box_pack_end(GTK_BOX(o_box), pair_now, FALSE, FALSE, 0);

      gtk_container_add(GTK_CONTAINER(offer_btn), o_box);
      g_object_set_data_full(G_OBJECT(offer_btn), "offer_id",
                             g_strdup(offer.id.c_str()), g_free);
      g_object_set_data(G_OBJECT(offer_btn), "shell_ptr", this);
      g_signal_connect(offer_btn, "clicked",
                       G_CALLBACK(+[](GtkButton* b, gpointer) {
                         auto* self = static_cast<GtkShellImpl*>(
                             g_object_get_data(G_OBJECT(b), "shell_ptr"));
                         const char* oid = static_cast<const char*>(
                             g_object_get_data(G_OBJECT(b), "offer_id"));
                         if (self != nullptr && oid != nullptr) {
                           for (const auto& off : self->snapshot_.pairing_offers) {
                             if (off.id == oid) {
                               self->ShowPairingOffer(off);
                               return;
                             }
                           }
                           self->ShowPairingPage();
                         }
                       }),
                       nullptr);

      gtk_box_pack_start(GTK_BOX(device_list_box_), offer_btn, FALSE, FALSE, 0);
    }

    gtk_widget_show_all(device_list_box_);

    // 5. Update Bubble tooltip & redraw
    if (bubble_window_ != nullptr) {
      gtk_widget_set_tooltip_text(bubble_window_,
                                  snapshot_.health.tray_tooltip.c_str());
      if (bubble_drawing_area_ != nullptr) {
        gtk_widget_queue_draw(bubble_drawing_area_);
      }
    }

    // 6. Update Pairing Status if active
    if (snapshot_.pairing.has_value() && pairing_status_label_ != nullptr) {
      const auto& p = *snapshot_.pairing;
      std::string text = p.title;
      if (p.detail.has_value() && !p.detail->empty()) {
        text += "\n" + *p.detail;
      }
      gtk_label_set_text(GTK_LABEL(pairing_status_label_), text.c_str());
      if (p.phase == core::PairingPhase::Pairing) {
        gtk_widget_set_sensitive(pairing_submit_btn_, FALSE);
      } else {
        gtk_widget_set_sensitive(pairing_submit_btn_, TRUE);
      }
      if (current_page_ == 2 &&
          p.phase == core::PairingPhase::PairedWaitingForDevice) {
        NavigateToPage(0);
      }
    }
  }

  void ShowDeviceDetails(const std::string& dev_id) {
    selected_device_id_ = dev_id;
    const core::DeviceView* found = nullptr;
    for (const auto& d : snapshot_.devices) {
      if (d.id == dev_id) {
        found = &d;
        break;
      }
    }
    if (found == nullptr) {
      NavigateToPage(0);
      return;
    }

    gtk_label_set_text(GTK_LABEL(details_name_label_),
                       found->display_name.c_str());

    std::string status_text = found->state_label;
    if (!found->transport_label.empty()) {
      status_text += " • " + found->transport_label;
    }
    gtk_label_set_text(GTK_LABEL(details_status_label_), status_text.c_str());

    if (found->guidance.has_value() && !found->guidance->empty()) {
      gtk_label_set_text(GTK_LABEL(details_guidance_label_),
                         found->guidance->c_str());
      gtk_widget_show_all(details_guidance_box_);
    } else {
      gtk_widget_hide(details_guidance_box_);
    }

    // Clear previous card rows
    GList* card_children =
        gtk_container_get_children(GTK_CONTAINER(details_card_box_));
    for (GList* l = card_children; l != nullptr; l = l->next) {
      gtk_widget_destroy(GTK_WIDGET(l->data));
    }
    g_list_free(card_children);

    auto add_property_row = [&](const std::string& label_text,
                                const std::string& value_text,
                                bool copyable = false) {
      GtkWidget* row = gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 8);
      gtk_widget_set_margin_top(row, 2);
      gtk_widget_set_margin_bottom(row, 2);

      GtkWidget* lbl = gtk_label_new(label_text.c_str());
      gtk_widget_set_size_request(lbl, 75, -1);
      gtk_label_set_xalign(GTK_LABEL(lbl), 0.0f);
      PangoAttrList* lattrs = pango_attr_list_new();
      pango_attr_list_insert(lattrs,
                             pango_attr_foreground_new(0x8888, 0x8888, 0x8888));
      gtk_label_set_attributes(GTK_LABEL(lbl), lattrs);
      pango_attr_list_unref(lattrs);
      gtk_box_pack_start(GTK_BOX(row), lbl, FALSE, FALSE, 0);

      GtkWidget* val = gtk_label_new(value_text.c_str());
      gtk_label_set_xalign(GTK_LABEL(val), 0.0f);
      gtk_label_set_selectable(GTK_LABEL(val), TRUE);
      gtk_box_pack_start(GTK_BOX(row), val, TRUE, TRUE, 0);

      if (copyable && !value_text.empty()) {
        GtkWidget* copy_btn = gtk_button_new_with_label("Copy");
        gtk_button_set_relief(GTK_BUTTON(copy_btn), GTK_RELIEF_HALF);
        g_object_set_data_full(G_OBJECT(copy_btn), "copy_val",
                               g_strdup(value_text.c_str()), g_free);
        g_signal_connect(copy_btn, "clicked",
                         G_CALLBACK(+[](GtkButton* b, gpointer) {
                           const char* str = static_cast<const char*>(
                               g_object_get_data(G_OBJECT(b), "copy_val"));
                           if (str != nullptr) {
                             GtkClipboard* clip =
                                 gtk_clipboard_get(GDK_SELECTION_CLIPBOARD);
                             gtk_clipboard_set_text(clip, str, -1);
                             gtk_button_set_label(b, "Copied!");
                           }
                         }),
                         nullptr);
        gtk_box_pack_end(GTK_BOX(row), copy_btn, FALSE, FALSE, 0);
      }

      gtk_box_pack_start(GTK_BOX(details_card_box_), row, FALSE, FALSE, 0);
    };

    if (found->detail.exact_model.has_value()) {
      add_property_row("Model", *found->detail.exact_model);
    }
    if (found->detail.android_version.has_value()) {
      add_property_row("Android", *found->detail.android_version);
    }
    if (found->detail.serial.has_value()) {
      add_property_row("Serial", *found->detail.serial, true);
    }
    add_property_row("Connection", found->transport_label);
    if (found->detail.address.has_value()) {
      add_property_row("Address", *found->detail.address, true);
    }
    if (found->battery_percent.has_value()) {
      add_property_row("Battery",
                       std::to_string(*found->battery_percent) + "%");
    }
    for (const auto& cf : found->detail.copy_fields) {
      if (cf.key != "serial" && cf.key != "address") {
        add_property_row(cf.label, cf.value, true);
      }
    }

    gtk_widget_show_all(details_card_box_);

    if (found->transport == core::TransportKind::WiFi) {
      gtk_widget_show(details_disconnect_btn_);
    } else {
      gtk_widget_hide(details_disconnect_btn_);
    }

    NavigateToPage(1);
  }

  void ShowPairingPage() override {
    current_pairing_offer_id_.clear();
    if (!snapshot_.pairing_offers.empty()) {
      const auto& off = snapshot_.pairing_offers.front();
      current_pairing_offer_id_ = off.id;
      if (!off.host_port.empty()) {
        gtk_entry_set_text(GTK_ENTRY(pairing_host_port_entry_),
                           off.host_port.c_str());
      } else {
        gtk_entry_set_text(GTK_ENTRY(pairing_host_port_entry_), "");
      }
      std::string status = "Device ready: " + off.display_name;
      gtk_label_set_text(GTK_LABEL(pairing_status_label_), status.c_str());
    } else {
      gtk_entry_set_text(GTK_ENTRY(pairing_host_port_entry_), "");
      gtk_label_set_text(GTK_LABEL(pairing_status_label_), "");
    }
    gtk_entry_set_text(GTK_ENTRY(pairing_code_entry_), "");
    gtk_widget_set_sensitive(pairing_submit_btn_, TRUE);
    NavigateToPage(2);
  }

  void ShowPairingOffer(const core::PairingOfferView& offer) {
    current_pairing_offer_id_ = offer.id;
    if (!offer.host_port.empty()) {
      gtk_entry_set_text(GTK_ENTRY(pairing_host_port_entry_),
                         offer.host_port.c_str());
    } else {
      gtk_entry_set_text(GTK_ENTRY(pairing_host_port_entry_), "");
    }
    gtk_entry_set_text(GTK_ENTRY(pairing_code_entry_), "");
    std::string status = "Device ready: " + offer.display_name;
    gtk_label_set_text(GTK_LABEL(pairing_status_label_), status.c_str());
    gtk_widget_set_sensitive(pairing_submit_btn_, TRUE);
    NavigateToPage(2);
  }

  void SubmitPairing() {
    const char* hp_text =
        gtk_entry_get_text(GTK_ENTRY(pairing_host_port_entry_));
    const char* code_text = gtk_entry_get_text(GTK_ENTRY(pairing_code_entry_));
    std::string hp = (hp_text != nullptr) ? hp_text : "";
    std::string code = (code_text != nullptr) ? code_text : "";

    while (!hp.empty() && (hp.front() == ' ' || hp.front() == '\t'))
      hp.erase(0, 1);
    while (!hp.empty() && (hp.back() == ' ' || hp.back() == '\t'))
      hp.pop_back();
    while (!code.empty() && (code.front() == ' ' || code.front() == '\t'))
      code.erase(0, 1);
    while (!code.empty() && (code.back() == ' ' || code.back() == '\t'))
      code.pop_back();

    size_t colon = hp.rfind(':');
    std::string host = (colon != std::string::npos) ? hp.substr(0, colon) : hp;
    int port = (colon != std::string::npos)
                   ? std::atoi(hp.substr(colon + 1).c_str())
                   : 0;

    if (host.empty() || port <= 0 || port > 65535 || code.size() != 6) {
      gtk_label_set_text(
          GTK_LABEL(pairing_status_label_),
          "Please enter a valid IP:port (e.g. 192.168.1.50:37281) and 6-digit code.");
      return;
    }

    std::string offer_id = current_pairing_offer_id_;
    std::string disp_name = "Android Device";
    if (offer_id.empty()) {
      for (const auto& off : snapshot_.pairing_offers) {
        if (off.host_port == hp || off.host_port.rfind(host + ":", 0) == 0) {
          offer_id = off.id;
          disp_name = off.display_name;
          break;
        }
      }
    } else {
      for (const auto& off : snapshot_.pairing_offers) {
        if (off.id == offer_id) {
          disp_name = off.display_name;
          break;
        }
      }
    }

    gtk_label_set_text(GTK_LABEL(pairing_status_label_),
                       "Pairing with device…");
    gtk_widget_set_sensitive(pairing_submit_btn_, FALSE);

    if (callbacks_.on_pair_device) {
      callbacks_.on_pair_device(offer_id, host, port, code, disp_name);
    }
  }

  void RepositionFlyout(int win_w, int win_h) {
    if (flyout_window_ == nullptr) return;
    if (win_w <= 1) win_w = 240;
    if (win_h <= 1) win_h = 130;

    core::PlacementInputs in{};
    in.dimensions = core::FlyoutDimensions{win_w, win_h, 8};

    GdkDisplay* display = gdk_display_get_default();
    if (display != nullptr) {
      int n_monitors = gdk_display_get_n_monitors(display);
      GdkRectangle primary_geom{};
      GdkRectangle primary_work{};
      bool has_primary = false;

      GdkMonitor* prim = gdk_display_get_primary_monitor(display);
      if (prim == nullptr && n_monitors > 0) {
        prim = gdk_display_get_monitor(display, 0);
      }

      for (int i = 0; i < n_monitors; ++i) {
        GdkMonitor* mon = gdk_display_get_monitor(display, i);
        if (mon != nullptr) {
          GdkRectangle geom{};
          GdkRectangle work{};
          gdk_monitor_get_geometry(mon, &geom);
          gdk_monitor_get_workarea(mon, &work);
          in.monitors.push_back(
              core::ScreenRect{geom.x, geom.y, geom.width, geom.height});
          if (mon == prim || (!has_primary && i == 0)) {
            in.workarea =
                core::ScreenRect{work.x, work.y, work.width, work.height};
            in.scale_factor = static_cast<double>(gdk_monitor_get_scale_factor(mon));
            primary_geom = geom;
            primary_work = work;
            has_primary = true;
          }
        }
      }

      if (last_known_tray_hint_.has_value()) {
        in.tray_hint = *last_known_tray_hint_;
      } else if (has_primary) {
        // Default permanent tray anchor: bottom-right corner of primary monitor workarea
        int tx = primary_work.x + primary_work.width - 20;
        int ty = primary_geom.y + primary_geom.height - 10;
        // If panel is on top (work.y > geom.y)
        if (primary_work.y > primary_geom.y) {
          ty = primary_geom.y + 10;
        }
        in.tray_hint = core::ScreenPoint{tx, ty};
        last_known_tray_hint_ = in.tray_hint;
      }
    }

    auto placement = core::ComputeFlyoutPlacement(in);
    target_anchor_x_ = placement.logical_position.x;
    target_anchor_y_ = placement.logical_position.y;
    bool panel_is_top = (in.tray_hint.y < in.workarea.y + in.workarea.height / 2);
    anim_offset_y_ = panel_is_top ? -16 : 16;

    if (anim_kind_ == SurfaceAnimKind::None) {
      gtk_window_move(GTK_WINDOW(flyout_window_),
                      placement.logical_position.x,
                      placement.logical_position.y);
    }
  }

  void ReanchorFlyout(int width, int height) {
    if (is_pinned_ || anim_kind_ != SurfaceAnimKind::None) {
      return;
    }
    if (flyout_visible_ || (flyout_window_ != nullptr && gtk_widget_get_visible(flyout_window_))) {
      RepositionFlyout(width, height);
    }
  }

  void UpdatePlacement(core::ScreenPoint hint) {
    if (hint.x != 0 || hint.y != 0) {
      last_known_tray_hint_ = hint;
    }
    if (is_pinned_) {
      if (gtk_widget_get_realized(flyout_window_)) {
        gtk_window_get_position(GTK_WINDOW(flyout_window_), &target_anchor_x_, &target_anchor_y_);
      }
      return;
    }

    int win_w = 240;
    int win_h = 130;
    if (gtk_widget_get_realized(flyout_window_)) {
      win_w = gtk_widget_get_allocated_width(flyout_window_);
      win_h = gtk_widget_get_allocated_height(flyout_window_);
    }
    RepositionFlyout(win_w, win_h);
  }

  void UpdateBubblePlacement() {
    if (bubble_window_ == nullptr) return;
    int bx = 100;
    int by = 100;
    GdkDisplay* display = gdk_display_get_default();
    if (display != nullptr) {
      GdkMonitor* mon = gdk_display_get_primary_monitor(display);
      if (mon == nullptr && gdk_display_get_n_monitors(display) > 0) {
        mon = gdk_display_get_monitor(display, 0);
      }
      if (mon != nullptr) {
        GdkRectangle work{};
        gdk_monitor_get_workarea(mon, &work);
        bx = work.x + work.width - 18 - 8;
        by = work.y + work.height - 18 - 8;
      }
    }
    gtk_window_move(GTK_WINDOW(bubble_window_), bx, by);
  }

  static gboolean OnFocusOutStatic(GtkWidget*, GdkEventFocus*, gpointer data) {
    auto* self = static_cast<GtkShellImpl*>(data);
    if (!self->is_pinned_) {
      self->HideFlyout();
    }
    return FALSE;
  }

  static gboolean OnKeyPressStatic(GtkWidget*, GdkEventKey* event, gpointer data) {
    auto* self = static_cast<GtkShellImpl*>(data);
    if (event->keyval == GDK_KEY_Escape) {
      if (self->current_page_ > 0) {
        self->NavigateToPage(0);
      } else {
        self->HideFlyout();
      }
      return TRUE;
    }
    return FALSE;
  }

  static gboolean OnDeleteStatic(GtkWidget*, GdkEvent*, gpointer data) {
    auto* self = static_cast<GtkShellImpl*>(data);
    self->HideFlyout();
    return TRUE;
  }

  static gboolean OnDrawBubbleStatic(GtkWidget* widget, cairo_t* cr, gpointer data) {
    auto* self = static_cast<GtkShellImpl*>(data);
    int width = gtk_widget_get_allocated_width(widget);
    int height = gtk_widget_get_allocated_height(widget);

    cairo_save(cr);
    cairo_set_operator(cr, CAIRO_OPERATOR_CLEAR);
    cairo_paint(cr);
    cairo_restore(cr);

    auto theme_kind = (self->theme_ == platform::ThemePreference::Light)
                          ? core::ThemeKind::Light
                          : core::ThemeKind::Dark;
    auto color = core::TrayMarkerColor(self->snapshot_.health.tray, theme_kind);

    double cx = width / 2.0;
    double cy = height / 2.0;
    double r = std::min(cx, cy) - 1.0;
    if (r < 2.0) r = 2.0;

    cairo_set_operator(cr, CAIRO_OPERATOR_OVER);
    cairo_arc(cr, cx, cy, r, 0, 2 * M_PI);
    cairo_set_source_rgba(cr, color.r / 255.0, color.g / 255.0, color.b / 255.0,
                          color.a / 255.0);
    cairo_fill_preserve(cr);

    // Subtle outline
    cairo_set_source_rgba(cr, 1.0, 1.0, 1.0, 0.4);
    cairo_set_line_width(cr, 1.0);
    cairo_stroke(cr);

    return TRUE;
  }

  static gboolean OnSurfaceAnimTickStatic(GtkWidget* widget,
                                          GdkFrameClock* clock,
                                          gpointer data) {
    auto* self = static_cast<GtkShellImpl*>(data);
    if (self == nullptr) return G_SOURCE_REMOVE;
    return self->OnSurfaceAnimTick(widget, clock);
  }

  gboolean OnSurfaceAnimTick(GtkWidget*, GdkFrameClock* clock) {
    if (anim_kind_ == SurfaceAnimKind::None || flyout_window_ == nullptr) {
      anim_tick_id_ = 0;
      return G_SOURCE_REMOVE;
    }

    gint64 now_us = gdk_frame_clock_get_frame_time(clock);
    if (anim_start_time_us_ == 0) {
      anim_start_time_us_ = now_us;
    }

    constexpr double kDurationUs = 167.0 * 1000.0;
    double elapsed_us = static_cast<double>(now_us - anim_start_time_us_);
    double linear_t = std::clamp(elapsed_us / kDurationUs, 0.0, 1.0);

    if (anim_kind_ == SurfaceAnimKind::Entering) {
      double t = anim_start_progress_ + (1.0 - anim_start_progress_) * linear_t;
      current_anim_progress_ = t;
      double eased = core::EvaluateShortSpatialEasing(std::clamp(t, 0.0, 1.0));
      double opacity = std::clamp(eased, 0.0, 1.0);
      int cur_y = target_anchor_y_ + static_cast<int>(std::round(static_cast<double>(anim_offset_y_) * (1.0 - eased)));
      gtk_window_move(GTK_WINDOW(flyout_window_), target_anchor_x_, cur_y);
      gtk_widget_set_opacity(flyout_window_, opacity);

      if (linear_t >= 1.0 || t >= 1.0) {
        gtk_window_move(GTK_WINDOW(flyout_window_), target_anchor_x_, target_anchor_y_);
        gtk_widget_set_opacity(flyout_window_, 1.0);
        anim_kind_ = SurfaceAnimKind::None;
        anim_tick_id_ = 0;
        PresentFlyoutWindow();
        return G_SOURCE_REMOVE;
      }
    } else if (anim_kind_ == SurfaceAnimKind::Exiting) {
      double t = anim_start_progress_ + (1.0 - anim_start_progress_) * linear_t;
      current_anim_progress_ = t;
      double eased = core::EvaluateShortSpatialEasing(std::clamp(t, 0.0, 1.0));
      double opacity = std::clamp(1.0 - eased, 0.0, 1.0);
      int cur_y = target_anchor_y_ + static_cast<int>(std::round(static_cast<double>(anim_offset_y_) * eased));
      gtk_window_move(GTK_WINDOW(flyout_window_), target_anchor_x_, cur_y);
      gtk_widget_set_opacity(flyout_window_, opacity);

      if (linear_t >= 1.0 || t >= 1.0) {
        gtk_widget_hide(flyout_window_);
        gtk_widget_set_opacity(flyout_window_, 1.0);
        flyout_visible_ = false;
        anim_kind_ = SurfaceAnimKind::None;
        anim_tick_id_ = 0;
        RestoreBubble();
        return G_SOURCE_REMOVE;
      }
    }

    return G_SOURCE_CONTINUE;
  }

  bool IsReducedMotion() const {
    if (snapshot_.shell.reduced_motion) return true;
    GtkSettings* settings = gtk_settings_get_default();
    if (settings != nullptr) {
      gboolean anim = TRUE;
      g_object_get(settings, "gtk-enable-animations", &anim, nullptr);
      if (!anim) return true;
    }
    return false;
  }

  enum class SurfaceAnimKind {
    None,
    Entering,
    Exiting,
  };

  SurfaceAnimKind anim_kind_ = SurfaceAnimKind::None;
  guint anim_tick_id_ = 0;
  gint64 anim_start_time_us_ = 0;
  int target_anchor_x_ = 0;
  int target_anchor_y_ = 0;
  int anim_offset_y_ = 16;
  double anim_start_progress_ = 0.0;
  double current_anim_progress_ = 0.0;

  ShellCallbacks callbacks_;
  core::AppSnapshot snapshot_ = core::MakeInitialSnapshot(0);
  platform::ThemePreference theme_ = platform::ThemePreference::Dark;
  std::string system_theme_name_;
  std::optional<core::ScreenPoint> last_known_tray_hint_;

  GtkWidget* flyout_window_ = nullptr;
  GtkWidget* bubble_window_ = nullptr;
  GtkWidget* bubble_drawing_area_ = nullptr;
  GtkWidget* stack_ = nullptr;
  GtkWidget* back_button_ = nullptr;
  GtkWidget* pin_button_ = nullptr;
  GtkWidget* summary_label_ = nullptr;
  GtkWidget* device_list_box_ = nullptr;
  GtkWidget* outage_box_ = nullptr;
  GtkWidget* outage_label_ = nullptr;
  GtkWidget* outage_restart_btn_ = nullptr;
  GtkWidget* details_name_label_ = nullptr;
  GtkWidget* details_status_label_ = nullptr;
  GtkWidget* details_guidance_box_ = nullptr;
  GtkWidget* details_guidance_label_ = nullptr;
  GtkWidget* details_card_box_ = nullptr;
  GtkWidget* details_disconnect_btn_ = nullptr;

  GtkWidget* pairing_host_port_entry_ = nullptr;
  GtkWidget* pairing_code_entry_ = nullptr;
  GtkWidget* pairing_status_label_ = nullptr;
  GtkWidget* pairing_submit_btn_ = nullptr;
  GtkWidget* pairing_cancel_btn_ = nullptr;
  std::string current_pairing_offer_id_;
  std::string selected_device_id_;
  int current_page_ = 0;
  bool is_pinned_ = false;
  bool flyout_visible_ = false;
  bool bubble_visible_ = false;
  bool bubble_enabled_ = false;
  int64_t last_deactivate_hide_ms_ = 0;
  int last_allocated_w_ = 0;
  int last_allocated_h_ = 0;
};

}  // namespace

std::unique_ptr<IShell> CreateGtkShell(ShellCallbacks callbacks,
                                       int* argc,
                                       char*** argv) {
  if (!gtk_init_check(argc, argv)) {
    return nullptr;
  }
  return std::make_unique<GtkShellImpl>(std::move(callbacks));
}

}  // namespace adbhawk::shell
