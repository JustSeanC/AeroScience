#include <Windows.h>
#include <SimConnect.h>
// Compile against the installed official SDK before compiling/packaging the toolbar.
static_assert(SIMCONNECT_RECV_ID_COMM_BUS == 43);
static_assert(SIMCONNECT_RECV_ID_SIMOBJECT_DATA == 8);
static_assert(SIMCONNECT_DATATYPE_STRING256 == 9);
static_assert(SIMCONNECT_DATATYPE_FLOAT64 == 4);
static_assert(SIMCONNECT_PERIOD_SECOND == 4);
static_assert(SIMCONNECT_COMM_BUS_BROADCAST_TO_JS == 1);
static_assert(sizeof(SIMCONNECT_RECV_COMM_BUS) == 32);
static_assert(sizeof(SIMCONNECT_RECV_SIMOBJECT_DATA) == 44);
void verify_exports() {
    (void)&SimConnect_SubscribeToCommBusEvent;
    (void)&SimConnect_CallCommBusEvent;
    (void)&SimConnect_CallDispatch;
}
