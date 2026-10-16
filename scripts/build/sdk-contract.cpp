#include <Windows.h>
#include <SimConnect.h>
#include <cstddef>
#include <type_traits>
#include <tuple>
// Compile against the installed official SDK before compiling/packaging the toolbar.
static_assert(sizeof(void*) == 8, "Use the x64 Native Tools compiler.");
static_assert(sizeof(DWORD) == 4);
static_assert(sizeof(HRESULT) == 4);
static_assert(SIMCONNECT_RECV_ID_COMM_BUS == 43);
static_assert(SIMCONNECT_RECV_ID_SIMOBJECT_DATA == 8);
static_assert(SIMCONNECT_DATATYPE_STRING256 == 9);
static_assert(SIMCONNECT_DATATYPE_FLOAT64 == 4);
static_assert(SIMCONNECT_PERIOD_SECOND == 4);
static_assert(SIMCONNECT_COMM_BUS_BROADCAST_TO_JS == 1);
// rgData is a variable-length string member, so sizeof includes its declaration
// (and possibly tail padding). The decoder needs the member offsets, not sizeof.
static_assert(offsetof(SIMCONNECT_RECV_COMM_BUS, dwEntryNumber) == 20, "CommBus fragment index must be at byte 20.");
static_assert(offsetof(SIMCONNECT_RECV_COMM_BUS, dwOutOf) == 24, "CommBus fragment count must be at byte 24.");
static_assert(offsetof(SIMCONNECT_RECV_COMM_BUS, uEventID) == 28, "CommBus subscription ID must be at byte 28.");
static_assert(offsetof(SIMCONNECT_RECV_COMM_BUS, rgData) == 32, "CommBus payload must start at byte 32.");
static_assert(sizeof(SIMCONNECT_RECV_SIMOBJECT_DATA) == 44);
static_assert(offsetof(SIMCONNECT_RECV_SIMOBJECT_DATA, dwData) == 40, "Aircraft telemetry payload must start at byte 40.");
template <typename> struct SdkFunction;
template <typename R, typename... Args> struct SdkFunction<R (WINAPI*)(Args...)> {
    using Arguments = std::tuple<Args...>;
};
using CommBusArguments = SdkFunction<decltype(&SimConnect_CallCommBusEvent)>::Arguments;
static_assert(std::tuple_size_v<CommBusArguments> == 5);
static_assert(sizeof(std::tuple_element_t<2, CommBusArguments>) == 4, "CommBus broadcast flags must match the 32-bit P/Invoke.");
static_assert(sizeof(std::tuple_element_t<3, CommBusArguments>) == 4, "CommBus byte count must match the 32-bit P/Invoke.");
// Compile-call checks against real installed SDK declarations. These do not connect to MSFS.
static_assert(std::is_same_v<decltype(SimConnect_Open(nullptr, "AeroScience", nullptr, DWORD{0}, nullptr, DWORD{0})), HRESULT>);
static_assert(std::is_same_v<decltype(SimConnect_AddToDataDefinition(nullptr, DWORD{1}, "TITLE", nullptr, SIMCONNECT_DATATYPE_STRING256, 0.0f, DWORD{0})), HRESULT>);
static_assert(std::is_same_v<decltype(SimConnect_RequestDataOnSimObject(nullptr, DWORD{2}, DWORD{1}, DWORD{0}, SIMCONNECT_PERIOD_SECOND, DWORD{0}, DWORD{0}, DWORD{0}, DWORD{0})), HRESULT>);
static_assert(std::is_same_v<decltype(SimConnect_SubscribeToCommBusEvent(nullptr, DWORD{100}, "AeroScience.Command.v1")), HRESULT>);
static_assert(std::is_same_v<decltype(SimConnect_CallCommBusEvent(nullptr, "AeroScience.Reply.v1", SIMCONNECT_COMM_BUS_BROADCAST_TO_JS, DWORD{3}, "{}")), HRESULT>);
void verify_exports() {
    (void)&SimConnect_SubscribeToCommBusEvent;
    (void)&SimConnect_CallCommBusEvent;
    (void)&SimConnect_CallDispatch;
}
