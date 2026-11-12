// Read-only, local LAS/LAZ worker. LASzip source is pinned separately (Apache-2.0).
// No network client, shell, dynamic plugin, output file, or source mutation.
#include "laszip/dll/laszip_api.h"
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <stdexcept>
#include <string>
#include <limits>
#ifdef _WIN32
#include <windows.h>
#include <fcntl.h>
#include <io.h>
#else
#include <sys/resource.h>
#include <sys/prctl.h>
#include <unistd.h>
#endif
#ifndef AS_LIDAR_ID
#error Missing verified build identity
#endif
static void limits() {
#ifdef _WIN32
    HANDLE job=CreateJobObjectW(nullptr,nullptr);
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION info{};
    info.BasicLimitInformation.LimitFlags=JOB_OBJECT_LIMIT_PROCESS_MEMORY|JOB_OBJECT_LIMIT_ACTIVE_PROCESS|JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE|JOB_OBJECT_LIMIT_PROCESS_TIME;
    info.BasicLimitInformation.ActiveProcessLimit=1;
    info.BasicLimitInformation.PerProcessUserTimeLimit.QuadPart=60LL*10000000;
    info.ProcessMemoryLimit=256ULL*1024*1024;
    if(!job||!SetInformationJobObject(job,JobObjectExtendedLimitInformation,&info,sizeof(info))||!AssignProcessToJobObject(job,GetCurrentProcess())) throw std::runtime_error("isolation");
    // Keep the job handle for the lifetime of this single process.
    SetErrorMode(SEM_FAILCRITICALERRORS|SEM_NOGPFAULTERRORBOX);
    _setmode(_fileno(stdout),_O_BINARY);
#else
    auto bound=[](int kind,rlim_t n){rlimit old{};if(getrlimit(kind,&old))throw std::runtime_error("isolation");rlimit v{n<old.rlim_cur?n:old.rlim_cur,n<old.rlim_max?n:old.rlim_max};if(setrlimit(kind,&v))throw std::runtime_error("isolation");};
    bound(RLIMIT_AS,256ULL*1024*1024);bound(RLIMIT_DATA,256ULL*1024*1024);bound(RLIMIT_CPU,60);bound(RLIMIT_CORE,0);bound(RLIMIT_FSIZE,0);
    if(prctl(PR_SET_NO_NEW_PRIVS,1,0,0,0))throw std::runtime_error("isolation");
#endif
}
template<class T>static void put(T v){if(fwrite(&v,sizeof(v),1,stdout)!=1)throw std::runtime_error("output");}
static uint64_t number(const char* s){if(!s||!*s)throw std::runtime_error("argument");uint64_t n=0;for(;*s;s++){if(*s<'0'||*s>'9'||n>20000000)throw std::runtime_error("argument");n=n*10+(*s-'0');}return n;}
static int run(int argc,char** argv) {
    try {
        limits();
        if(argc==2&&std::string(argv[1])=="--identity"){puts("AS-LIDAR-1 " AS_LIDAR_ID);return 0;}
        if(argc!=5||std::string(argv[1])!="--read-v1")throw std::runtime_error("argument");
        auto first=number(argv[3]),count=number(argv[4]);if(first>20000000||count<1||count>4096)throw std::runtime_error("limit");
        laszip_POINTER reader=nullptr;laszip_header_struct* h=nullptr;laszip_point_struct* p=nullptr;laszip_BOOL compressed=0;
        if(laszip_create(&reader)||laszip_open_reader(reader,argv[2],&compressed)||laszip_get_header_pointer(reader,&h)||laszip_get_point_pointer(reader,&p))throw std::runtime_error("format");
        auto total=h->extended_number_of_point_records?h->extended_number_of_point_records:h->number_of_point_records;
        auto format=h->point_data_format;
        if(h->version_major!=1||h->version_minor>4||format>8||format==4||format==5||total>20000000||first>total||count>total-first||h->point_data_record_length>2048)throw std::runtime_error("limit");
        if(laszip_seek_point(reader,(laszip_I64)first))throw std::runtime_error("seek");
        if(fwrite("ASLIDAR1",1,8,stdout)!=8)throw std::runtime_error("output");put((uint32_t)count);
        for(uint64_t i=0;i<count;i++){
            if(laszip_read_point(reader))throw std::runtime_error("record");
            if(p->num_extra_bytes<0||p->num_extra_bytes>2048)throw std::runtime_error("extra");
            put((int32_t)p->X);put((int32_t)p->Y);put((int32_t)p->Z);put((uint16_t)p->intensity);
            put((uint8_t)(format>=6?p->extended_return_number:p->return_number));put((uint8_t)(format>=6?p->extended_number_of_returns:p->number_of_returns));
            put((uint8_t)(format>=6?p->extended_classification:p->classification));
            put((uint8_t)(format>=6?p->extended_classification_flags:(p->synthetic_flag|(p->keypoint_flag<<1)|(p->withheld_flag<<2))));
            put((uint8_t)p->scan_direction_flag);put((uint8_t)p->edge_of_flight_line);put((uint8_t)p->user_data);put((uint16_t)p->point_source_ID);
            put((int16_t)(format>=6?p->extended_scan_angle:p->scan_angle_rank));put((uint8_t)(format>=6?p->extended_scanner_channel:0));
            put((double)p->gps_time);for(int c=0;c<4;c++)put((uint16_t)p->rgb[c]);
            put((uint16_t)p->num_extra_bytes);if(p->num_extra_bytes&&fwrite(p->extra_bytes,1,p->num_extra_bytes,stdout)!=(size_t)p->num_extra_bytes)throw std::runtime_error("output");
        }
        if(laszip_close_reader(reader)||laszip_destroy(reader)||fflush(stdout))throw std::runtime_error("close");return 0;
    }catch(...){fputs("Bounded LAS/LAZ reader refused the input or exceeded its resource/protocol limits.\n",stderr);return 2;}
}
#ifdef _WIN32
int wmain(int argc,wchar_t** wide){char** args=new char*[argc];for(int i=0;i<argc;i++){int n=WideCharToMultiByte(CP_UTF8,WC_ERR_INVALID_CHARS,wide[i],-1,nullptr,0,nullptr,nullptr);if(n<=0)return 2;args[i]=new char[n];WideCharToMultiByte(CP_UTF8,WC_ERR_INVALID_CHARS,wide[i],-1,args[i],n,nullptr,nullptr);}return run(argc,args);}
#else
int main(int argc,char** argv){return run(argc,argv);}
#endif
