// Linux J2534-1 compatibility backend for OBDX Pro GT DVI / J1850 VPW.
// Experimental, no flashing support. Public J2534 entrypoints use fixed 32-bit fields.
#define _POSIX_C_SOURCE 200809L
#include <stdint.h>
#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <fcntl.h>
#include <termios.h>
#include <poll.h>
#include <errno.h>
#include <time.h>
#include <pthread.h>
typedef uint32_t U32;
typedef struct { U32 ProtocolID,RxStatus,TxFlags,Timestamp,DataSize,ExtraDataIndex; unsigned char Data[4128]; } PASSTHRU_MSG;
enum { OK=0, UNSUPPORTED=1, BAD_CHANNEL=2, BAD_PROTOCOL=3, NULL_ARG=4, BAD_IOCTL_VALUE=5, BAD_FLAGS=6, FAILED=7, NOT_CONNECTED=8, TIMEOUT=9, BAD_MSG=10, BAD_INTERVAL=11, LIMIT=12, BAD_MSG_ID=13, IN_USE=14, BAD_IOCTL=15, EMPTY=16, FULL=17, OVERFLOW=18, BAD_PIN=19, CHANNEL_IN_USE=20, MSG_PROTOCOL=21, BAD_FILTER=22, NO_FLOW=23, NOT_UNIQUE=24, BAD_BAUD=25, BAD_DEVICE=26 };
static pthread_mutex_t lock = PTHREAD_MUTEX_INITIALIZER;
static int fd=-1,connected=0; static U32 device_id=1,channel_id=1;
static char last_error[160]="", firmware[80]="OBDX Pro GT", revision[80]="0.1 DVI Linux";
static int64_t now_ms(void){struct timespec t;clock_gettime(CLOCK_MONOTONIC,&t);return (int64_t)t.tv_sec*1000+t.tv_nsec/1000000;}
static long error(long v,const char *s){if(s)snprintf(last_error,sizeof last_error,"%s",s);return v;}
static int read_byte(int timeout){if(timeout<0)timeout=0;struct pollfd p={.fd=fd,.events=POLLIN};int x=poll(&p,1,timeout);if(x<=0)return -1;unsigned char c;if(read(fd,&c,1)!=1)return -1;return c;}
static int write_all(const unsigned char *p,size_t n){while(n){ssize_t k=write(fd,p,n);if(k<0&&errno==EINTR)continue;if(k<=0)return -1;p+=k;n-=k;}return 0;}
static int dvi_send(unsigned char cmd,const unsigned char *data,size_t n){
 if(n>255)return -1;unsigned char b[260];b[0]=cmd;b[1]=n;unsigned sum=cmd+n;
 for(size_t i=0;i<n;i++){b[2+i]=data[i];sum+=data[i];}
 b[2+n]=(unsigned char)~sum;return write_all(b,n+3);
}
static int dvi_recv(unsigned char *cmd,unsigned char *data,size_t *len,int ms){
 int64_t until=now_ms()+ms;int x;do{x=read_byte((int)(until-now_ms()));}while(x<0&&now_ms()<until);
 if(x<0)return -1;*cmd=x;int n=read_byte((int)(until-now_ms()));if(n<0)return -1;unsigned sum=*cmd+(unsigned)n;
 if(*cmd==9){int low=read_byte((int)(until-now_ms()));if(low<0)return -1;sum+=low;n=n*256+low;}
 if(n>4096||n<0)return -2;
 for(int i=0;i<n;i++){x=read_byte((int)(until-now_ms()));if(x<0)return -1;if(i<(int)*len)data[i]=(unsigned char)x;sum+=(unsigned)x;}
 x=read_byte((int)(until-now_ms()));if(x<0||((sum+(unsigned)x)&255)!=255)return -2;
 if(n>(int)*len)return -2;*len=n;return 0;
}
static int dvi_command(unsigned char cmd,const unsigned char *data,size_t n){
 if(dvi_send(cmd,data,n))return -1;
 int64_t until=now_ms()+1500;unsigned char reply[4096],rc;size_t z;
 while(now_ms()<until){z=sizeof reply;if(dvi_recv(&rc,reply,&z,(int)(until-now_ms())))return -1;
 if(rc==0x08||rc==0x09)continue;
 if(rc==0x7f)return -1;
 return rc==(unsigned char)(cmd+0x10)?0:-1;
 }return -1;
}
static int wait_text(const char *text,int ms){
 if(write_all((const unsigned char*)text,strlen(text)))return -1;int64_t until=now_ms()+ms;char tmp[256];size_t n=0;
 while(now_ms()<until){int c=read_byte((int)(until-now_ms()));if(c<0)break;if(n+1<sizeof tmp)tmp[n++]=c;if(c=='>'){tmp[n]=0;return strstr(tmp,"OK")!=NULL ? 0 : -1;}}
 return -1;
}
static int initialize(const char *path){
 fd=open(path,O_RDWR|O_NOCTTY|O_CLOEXEC|O_NONBLOCK);if(fd<0)return -1;
 struct termios t;if(tcgetattr(fd,&t)<0)goto bad;
 cfmakeraw(&t);cfsetispeed(&t,B115200);cfsetospeed(&t,B115200);t.c_cflag|=CLOCAL|CREAD;
 if(tcsetattr(fd,TCSANOW,&t)<0)goto bad;
 // OBDX DVI handshake, compatible with the scanner's established GT path.
 tcflush(fd,TCIOFLUSH);
 dvi_send(0x25,NULL,0);struct timespec pause={0,250000000};nanosleep(&pause,NULL);tcflush(fd,TCIOFLUSH);
 wait_text("ATZ\r",1100);tcflush(fd,TCIFLUSH);
 if(wait_text("DXDP1\r",1500)<0)goto bad;
 return 0;
bad:close(fd);fd=-1;return -1;
}
long PassThruOpen(const void *name,U32 *out){
 if(!out)return error(NULL_ARG,"Null DeviceID");pthread_mutex_lock(&lock);
 if(fd>=0){pthread_mutex_unlock(&lock);return error(IN_USE,"GT already open");}
 const char *path=name?(const char*)name:getenv("OBDX_GT_PORT");
 if(!path||strncmp(path,"/dev/",5))path="/dev/ttyUSB0";
 int ok=initialize(path);if(!ok)*out=device_id;
 pthread_mutex_unlock(&lock);return ok?error(NOT_CONNECTED,"Cannot open or initialize GT in DVI mode"):OK;
}
long PassThruClose(U32 id){pthread_mutex_lock(&lock);if(fd<0||id!=device_id){pthread_mutex_unlock(&lock);return BAD_DEVICE;}dvi_send(0x25,NULL,0);close(fd);fd=-1;connected=0;pthread_mutex_unlock(&lock);return OK;}
long PassThruConnect(U32 id,U32 protocol,U32 flags,U32 baud,U32 *out){
 if(!out)return NULL_ARG;if(id!=device_id||fd<0)return NOT_CONNECTED;
 if(protocol!=1)return error(BAD_PROTOCOL,"Only J1850VPW is implemented");if(flags)return BAD_FLAGS;
 if(baud!=10400&&baud!=0)return BAD_BAUD;pthread_mutex_lock(&lock);
 if(connected){pthread_mutex_unlock(&lock);return CHANNEL_IN_USE;}
 const unsigned char vpw[]={1,1},tester[]={0,0xf0,1},enable[]={2,1};
 int ok=dvi_command(0x31,vpw,sizeof vpw)||dvi_command(0x33,tester,sizeof tester)||dvi_command(0x31,enable,sizeof enable);
 if(!ok){connected=1;*out=channel_id;}pthread_mutex_unlock(&lock);
 return ok?error(FAILED,"DVI VPW setup rejected"):OK;
}
long PassThruDisconnect(U32 id){if(id!=channel_id||!connected)return BAD_CHANNEL;pthread_mutex_lock(&lock);connected=0;pthread_mutex_unlock(&lock);return OK;}
long PassThruWriteMsgs(U32 id,const PASSTHRU_MSG *msg,U32 *count,U32 timeout){
 (void)timeout;if(!count||!msg)return NULL_ARG;if(!connected||id!=channel_id)return BAD_CHANNEL;
 U32 wanted=*count;*count=0;pthread_mutex_lock(&lock);
 for(U32 i=0;i<wanted;i++){
  if(msg[i].ProtocolID!=1||msg[i].DataSize<4||msg[i].DataSize>255){pthread_mutex_unlock(&lock);return BAD_MSG;}
  // J2534 VPW carries a four-byte header with a leading zero; DVI takes the on-wire 3-byte header.
  if(msg[i].Data[0]!=0){pthread_mutex_unlock(&lock);return BAD_MSG;}
  if(dvi_send(0x10,msg[i].Data+1,msg[i].DataSize-1)){pthread_mutex_unlock(&lock);return error(FAILED,"DVI TX failed");}
  ++*count;
 }
 pthread_mutex_unlock(&lock);return OK;
}
long PassThruReadMsgs(U32 id,PASSTHRU_MSG *msg,U32 *count,U32 timeout){
 if(!count||!msg)return NULL_ARG;if(!connected||id!=channel_id)return BAD_CHANNEL;
 U32 want=*count;*count=0;if(!want)return OK;
 pthread_mutex_lock(&lock);int64_t until=now_ms()+timeout;
 while(*count<want){
  unsigned char cmd=0,buf[4096];size_t n=sizeof buf;
  int remain=(int)(until-now_ms());if(remain<0)remain=0;
  int ret=dvi_recv(&cmd,buf,&n,remain);
  if(ret)break;if((cmd!=8&&cmd!=9)||n<4||buf[2]!=0x10)continue;
  PASSTHRU_MSG *m=&msg[*count];memset(m,0,sizeof *m);m->ProtocolID=1;
  if(n+1>sizeof m->Data)continue;
  m->Data[0]=0;memcpy(m->Data+1,buf,n);m->DataSize=n+1;m->Timestamp=(U32)now_ms();++*count;
 }
 pthread_mutex_unlock(&lock);return *count?OK:EMPTY;
}
long PassThruReadVersion(U32 id,char *fw,char *dll,char *api){
 if(fd<0||id!=device_id)return BAD_DEVICE;if(!fw||!dll||!api)return NULL_ARG;
 snprintf(fw,80,"%s",firmware);snprintf(dll,80,"%s",revision);snprintf(api,80,"04.04");return OK;
}
long PassThruGetLastError(char *s){if(!s)return NULL_ARG;snprintf(s,80,"%.79s",last_error);return OK;}
long PassThruStartMsgFilter(U32 id,U32 type,const PASSTHRU_MSG *mask,const PASSTHRU_MSG *pattern,const PASSTHRU_MSG *flow,U32 *out){
 (void)mask;(void)pattern;(void)flow;if(id!=channel_id||!connected)return BAD_CHANNEL;if(!out)return NULL_ARG;
 // Do not falsely report active filtering without enforcing it.
 (void)type;return error(UNSUPPORTED,"J2534 message filtering not implemented");
}
long PassThruStopMsgFilter(U32 id,U32 filter){(void)id;(void)filter;return UNSUPPORTED;}
long PassThruStartPeriodicMsg(U32 id,const PASSTHRU_MSG *msg,U32 *out,U32 period){(void)id;(void)msg;(void)out;(void)period;return UNSUPPORTED;}
long PassThruStopPeriodicMsg(U32 id,U32 mid){(void)id;(void)mid;return UNSUPPORTED;}
long PassThruIoctl(U32 id,U32 code,const void *in,void *out){(void)id;(void)code;(void)in;(void)out;return UNSUPPORTED;}
long PassThruSetProgrammingVoltage(U32 id,U32 pin,U32 voltage){(void)id;(void)pin;(void)voltage;return UNSUPPORTED;}
