set(VCPKG_TARGET_ARCHITECTURE x64)
set(VCPKG_CRT_LINKAGE static)
set(VCPKG_LIBRARY_LINKAGE dynamic)
set(VCPKG_BUILD_TYPE release)

# libjpeg-turbo deliberately forces its shared libjpeg API DLL to /MD even when
# WITH_CRT_DLL=OFF. Use its supported static-library configuration instead, so
# TIFF links the codec into its own /MT module rather than sharing FILE*/CRT state.
# Do not patch upstream runtime flags or whitelist an unshipped Microsoft runtime.
if(PORT STREQUAL "libjpeg-turbo")
    set(VCPKG_LIBRARY_LINKAGE static)
    set(VCPKG_CMAKE_CONFIGURE_OPTIONS -DWITH_TURBOJPEG=OFF)
endif()
