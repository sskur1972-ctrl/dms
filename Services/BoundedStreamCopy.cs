namespace DMS.Web.Services;
public static class BoundedStreamCopy {
 public static async Task<long> CopyAsync(Stream source,Stream target,long limit,CancellationToken ct){
  if(limit<0)throw new ArgumentOutOfRangeException(nameof(limit));
  long size=0;var buffer=new byte[81920];int count;
  while((count=await source.ReadAsync(buffer.AsMemory(),ct))!=0){
   if(size>limit-count)throw new StoredFileTooLargeException(limit);
   await target.WriteAsync(buffer.AsMemory(0,count),ct);size+=count;
  }
  return size;
 }
}
