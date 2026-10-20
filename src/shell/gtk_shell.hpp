#pragma once

#include <memory>
#include "shell/shell.hpp"

namespace adbhawk::shell {

// Factory function to create a GTK 3.24 shell implementation of IShell.
// Calls `gtk_init_check`. Returns nullptr if display connection cannot be opened (e.g. headless).
std::unique_ptr<IShell> CreateGtkShell(ShellCallbacks callbacks,
                                       int* argc = nullptr,
                                       char*** argv = nullptr);

}  // namespace adbhawk::shell
