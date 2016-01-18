using System;
using System.Collections.Generic;
using System.Text;

using System.IO;

namespace MemMap
{
    public abstract class MemSched
    {
        //statistics counters
        public int[,] streak_length = new int[Config.N, 17]; 
        public int pid_last_req;
        public int last_streak_length = 1;
        public bool[] proc_done = new bool[Config.N];
        public int[] request_count = new int[Config.N];
        public int[,]interval_request_count = new int[Config.N, 101];
        
        //memory controller
        public MetaMemCtrl meta_mctrl;

        public virtual void initialize() { }
        public virtual void issue_req(Req req) { }
        public abstract void dequeue_req(Req req);
        public abstract void enqueue_req(Req req);

        //scheduler-specific overridden method
        public abstract Req better_req(Req req1, Req req2);
        
        public virtual void tick() 
        {
            return;
        }

        protected bool is_row_hit(Req req)
        {
            return meta_mctrl.is_row_hit(req);
        }

        public virtual void service_counter(Req req)
        {
            return;
        }

        
        public virtual void count_queueing(Cmd cmd)
        {
            if (cmd == null) return;

            return;
        }

        public virtual void set_proc_done(int pid)
        {
            proc_done[pid] = true;
        }
 
        public virtual void count_streaks(Req req)
        {
            if (pid_last_req != req.pid) 
            {
                if (!proc_done[pid_last_req])
                {
                    if (last_streak_length < 16) streak_length[pid_last_req, last_streak_length] ++;
                    else streak_length[pid_last_req, 16] ++;
                }

                last_streak_length = 1;
                pid_last_req = req.pid;
 
            }
            else last_streak_length ++;          
        }


        public virtual void print_streaks()
        {
            for (int p = 0; p < Config.N; p ++)
            {
                for (int i = 0; i < Config.N; i ++) 
                {
                    for (int j = 1; j < 17; j ++)
                    {
                        Console.WriteLine(" PID " + i + " streak length " + j +  " number of streaks " + streak_length[i,j] + "\n");
                    }
                }
            } 
        }

        public virtual void bus_interference_count(Cmd cmd)
        {
            return;
        }


        public virtual Req find_best_req(List<Req> q)
        {
            if (q.Count == 0)
                return null;

            Req best_req = q[0];
            for (int i = 1; i < q.Count; i++) {
                best_req = better_req(best_req, q[i]);
            }
            return best_req;
        }
    }
}
