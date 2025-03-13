using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Logging;
using Serilog.Core;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;

namespace Basip
{
    public class Worker : BackgroundService
    {
        public readonly ILogger logger;
        private WorkerOptions options;
        public TimeSpan timeout;
        public TimeSpan timestart;
        public TimeSpan deltasleep;
        public Worker(ILogger<Worker> logger, WorkerOptions options)
        {
            this.logger = logger;
            this.options = options;
            var time = options.timeout.Split(':');
            timeout = new TimeSpan(Int32.Parse(time[0]), Int32.Parse(time[1]), Int32.Parse(time[2]));
            time = options.timeout.Split(':');
            timestart = new TimeSpan(Int32.Parse(time[0]), Int32.Parse(time[1]), Int32.Parse(time[2]));
            var now = new TimeSpan(DateTime.Now.TimeOfDay.Hours, DateTime.Now.TimeOfDay.Minutes, DateTime.Now.TimeOfDay.Seconds);
            deltasleep = (options.run_now) ? TimeSpan.Zero :
                (timestart >= now) ? timestart - now : timestart - now + new TimeSpan(1, 0, 0, 0);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogTrace(@$"time run basip: {timestart} deltasleep: {deltasleep}");
            await Task.Delay(deltasleep);
            while (!stoppingToken.IsCancellationRequested)
            {
                logger.LogTrace($@"����� ��������");
                run();
                logger.LogTrace($@"timeout basip: {timeout}");
                await Task.Delay(timeout, stoppingToken);
            }
        }
        private void run()
        {
            DB db = new DB();
            FbConnection con=db.DBconnect(options.db_config);
            try
            {
                con.Open();
            }catch (Exception e)
            {
                logger.LogError("No connect database :"+ options.db_config);
                return;
            }
            logger.LogTrace("Ok connect database");
            List<Task> tasks = new List<Task>();
            Stopwatch stopwatch = Stopwatch.StartNew();
            //������ 
            DataRowCollection data = db.GetDevice().Rows;//�������� ������ �������, ����� � �������� � ��������
            con.Close();
            foreach (DataRow row in data)
                tasks.Add(TaskGet(row));//async ������� ����������� ��������
                //TaskGet(new Device(row), db).Wait();//not sync
            logger.LogDebug("device: "+data.Count);
            Task.WaitAll(tasks.ToArray());
            logger.LogDebug("time: "+stopwatch.ElapsedMilliseconds);
        }

        /* 12.03.2025 
         * ������ ������� ������ � �������
         * 
         * 
         */
        private async Task TaskGet(DataRow row)// � row ���������� �����, ������, id_dev, IP �����
        {
            DB db = new DB();
            FbConnection con = db.DBconnect(options.db_config);
            con.Open();
            Device dev =new Device(row);

            DeviceInfo deviceInfo= await dev.GetInfo(options.time_wait_http);//����� �� ��� �������� DeviceInfo? �����, ��� �������� � ����� Device?
            if (dev.is_online)// ����� � ������� ���
            {
                logger.LogDebug($@"No connect: id: {row["id_dev"]} ip: {dev.ip}");
                //db.saveParam((int)row["id_dev"], "ABOUT", null, "no connect");
                db.saveParam((int)row["id_dev"], "ONLINE", 0, null);//������������� ���������� ����� � �������.
                db.updateCaridxErrAll((int)row["id_dev"], "��� ����� � �����������");//��������� ������� ��� ���� ���� ���� ������
                return; 
            }
            else //����� � ������� ����
            {
                //������� ��������� ���� ��� ������
                string data = $@"{deviceInfo.device_model} , {deviceInfo.firmware_version} , {deviceInfo.firmware_version} , {deviceInfo.api_version}";
                logger.LogDebug((int)row["id_dev"] +" | "+data);
                db.saveParam((int)row["id_dev"], "ABOUT", null, data);//�������� ���������� � ������.
                db.saveParam((int)row["id_dev"], "ONLINE", 1, null);//�������� ������� �����
                //������� �����������
                
                if(dev.Auth(dev.password))
                {
                    //�������� ������� �� ������ ����
                    DataRowCollection cardList = db.GetCardForLoad((int)row["id_dev"]);//�������� ������ ���� ��� ������
                    if (cardList.Count>0)//���� ���� ����, �� �������� ������ � �������
                    {
                        //������ ��������������� � �������� ������
                        foreach(DataRow card in cardList)
                        {
                            //
                            if(card.attempt == 1)//������ �������������� � ������
                            {
                                if(dev.writekey(card.id))//���� ������ ������ �������, �� 
                                {
                                    db.delFromCardindev((int)row["id_dev"], card.id);//������� ����� �� ������� ��������
                                    db.updateCaridxOk((int)row["id_dev"], card.id);//�������� � ������� cardidx ���� � ����� �������� ������

                                } else
                                {
                                    string mess = "������� ��������� ������.";
                                    db.incrementCardindev((int)row["id_dev"], card.id);//���������� ������� �������� �� 1.
                                    db.updateCaridxErr((int)row["id_dev"], card.id, mess);//������������� ���������� ������� ������.

                                }

                            }
                            if(card.attempt == 2)//�������� ������������� �� ������
                            {
                                db.delFromCardindev((int)row["id_dev"], card.id);//������� ����� �� ������� cardindev
                                //� �������� cardidx ������ �� �������, �.�. ��� ���������� � ������ ��� ���
                                
                            } else
                            {
                                db.incrementCardindev((int)row["id_dev"], card.id);
                                //� �������� cardidx ������ �� �������, �.�. ��� ���������� � ������ ��� ���
                            }

                        }

                    } else
                    {
                        //��� �������. ��������, ��� ���� ������������� � ���-�����.
                    }

                    //���� �������
                    /*
                     * ��� ���� ������������ ���� ������ ������� �� ������ � ������ ������� � �� ����.
                     * 
                     * 
                     */
                } else //����������� ������ ���������
                {
                    //��������� � ��� ������ � ��������� �����������.
                    db.updateCaridxErrAll((int)row["id_dev"], card.id, "������ �����������.");//��������� ������� ��� ���� ���� ���� ������

                }

            }
            con.Close();
            //  Thread.Sleep(1000);
            // logger.LogDebug(dev.ip.ToString()+" | "+dev.id_dev_door0 + " | " +dev.id_dev_door1+" | "+dev.name+ " | " + dev.id_ctrl);
        }
    }
}
